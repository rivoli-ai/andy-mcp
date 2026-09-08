import assert from 'node:assert/strict';
import { Client } from '@modelcontextprotocol/sdk/client/index.js';
import { StdioClientTransport } from '@modelcontextprotocol/sdk/client/stdio.js';
import { StreamableHTTPClientTransport } from '@modelcontextprotocol/sdk/client/streamableHttp.js';
import { CreateMessageRequestSchema } from '@modelcontextprotocol/sdk/types.js';

const [mode, target] = process.argv.slice(2);
const transport = mode === 'stdio'
  ? new StdioClientTransport({ command: 'dotnet', args: [target, 'server'], stderr: 'inherit' })
  : new StreamableHTTPClientTransport(new URL(target));
const client = new Client({ name: 'pinned-official-client', version: '1' }, { capabilities: { sampling: {} } });
client.setRequestHandler(CreateMessageRequestSchema, async () => ({ role: 'assistant', model: 'reference', content: { type: 'text', text: 'sampled independently' } }));
try {
  await client.connect(transport);
  await client.ping();
  const tools = await client.listTools();
  assert(tools.tools.some(t => t.name === 'greet'));
  const result = await client.callTool({ name: 'greet', arguments: { name: '世界' } });
  assert(result.content[0].text.includes('世界'));
  const resources = await client.listResources();
  assert(resources.resources.length > 0);
  assert((await client.readResource({ uri: resources.resources[0].uri })).contents.length > 0);
  assert((await client.listPrompts()).prompts.some(p => p.name === 'explain'));
  assert((await client.getPrompt({ name: 'explain', arguments: { topic: 'MCP' } })).messages.length > 0);
  if (tools.tools.some(t => t.name === 'sample')) {
    const sample = await client.callTool({ name: 'sample', arguments: {} });
    assert.equal(sample.content[0].text, 'sampled independently');
  }
  console.log('REFERENCE_CLIENT_PASS');
} finally {
  if (mode !== 'stdio') await transport.terminateSession();
  await client.close();
}
