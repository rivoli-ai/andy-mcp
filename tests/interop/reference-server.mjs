import { Server } from '@modelcontextprotocol/sdk/server/index.js';
import { StdioServerTransport } from '@modelcontextprotocol/sdk/server/stdio.js';
import { StreamableHTTPServerTransport } from '@modelcontextprotocol/sdk/server/streamableHttp.js';
import { ListToolsRequestSchema, CallToolRequestSchema, ListResourcesRequestSchema, ReadResourceRequestSchema, ListPromptsRequestSchema, GetPromptRequestSchema } from '@modelcontextprotocol/sdk/types.js';
import { createServer } from 'node:http';
import { randomUUID } from 'node:crypto';

function makeServer() {
  const server = new Server({ name: 'pinned-official-sdk', version: '1' }, { capabilities: { tools: {}, resources: {}, prompts: {} } });
  server.setRequestHandler(ListToolsRequestSchema, async () => ({ tools: [
    { name: 'echo', inputSchema: { type: 'object', properties: { message: { type: 'string' } }, required: ['message'] } },
    { name: 'sample', inputSchema: { type: 'object' } }
  ] }));
  server.setRequestHandler(CallToolRequestSchema, async ({ params }) => {
    if (params.name === 'sample') {
      const sampled = await server.createMessage({ messages: [{ role: 'user', content: { type: 'text', text: 'sample' } }], maxTokens: 10 });
      return { content: Array.isArray(sampled.content) ? sampled.content : [sampled.content] };
    }
    return { content: [{ type: 'text', text: params.arguments.message }], _meta: { reference: true } };
  });
  server.setRequestHandler(ListResourcesRequestSchema, async () => ({ resources: [{ uri: 'file:///reference', name: 'reference' }] }));
  server.setRequestHandler(ReadResourceRequestSchema, async ({ params }) => ({ contents: [{ uri: params.uri, text: 'reference resource' }] }));
  server.setRequestHandler(ListPromptsRequestSchema, async () => ({ prompts: [{ name: 'reference' }] }));
  server.setRequestHandler(GetPromptRequestSchema, async () => ({ messages: [{ role: 'user', content: { type: 'text', text: 'reference prompt' } }] }));
  return server;
}

const mode = process.argv[2];
if (mode === 'stdio') await makeServer().connect(new StdioServerTransport());
else {
  const sessions = new Map();
  const http = createServer(async (req, res) => {
    try {
      let body;
      if (req.method === 'POST') {
        const chunks = [];
        for await (const chunk of req) chunks.push(chunk);
        body = JSON.parse(Buffer.concat(chunks).toString());
      }
      let transport = sessions.get(req.headers['mcp-session-id']);
      if (!transport && body?.method === 'initialize') {
        transport = new StreamableHTTPServerTransport({ sessionIdGenerator: randomUUID, enableJsonResponse: mode === 'json', onsessioninitialized: id => sessions.set(id, transport) });
        await makeServer().connect(transport);
      }
      if (!transport) { res.writeHead(404).end(); return; }
      await transport.handleRequest(req, res, body);
    } catch (error) { console.error(error); if (!res.headersSent) res.writeHead(500); res.end(); }
  });
  http.listen(0, '127.0.0.1', () => console.log(`http://127.0.0.1:${http.address().port}/mcp`));
  process.on('SIGTERM', async () => { for (const transport of sessions.values()) await transport.close(); http.close(); });
}
