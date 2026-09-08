import json,re,hashlib,subprocess
from pathlib import Path
sha='aa8ce049f089f92618340190d4ece141f663310d'
fixtures=[]
for version in ['2024-11-05','2025-06-18','2025-11-25']:
 for page in ['basic/lifecycle','server/tools','server/resources','client/sampling']:
  url=f'https://raw.githubusercontent.com/modelcontextprotocol/modelcontextprotocol/{sha}/docs/specification/{version}/{page}.mdx'
  source=subprocess.check_output(['curl','-fsSL',url]);last={}
  for index,block in enumerate(re.findall(r'```json[^\n]*\n(.*?)```',source.decode(),re.S)):
   try:value=json.loads(block)
   except json.JSONDecodeError:continue # Some illustrative fragments use comments or ellipses.
   if not isinstance(value,dict) or value.get('jsonrpc')!='2.0':continue
   if 'method' in value and 'id' in value:last[value['id']]=value['method']
   method=value.get('method',last.get(value.get('id')))
   if not method:continue
   fixtures.append({'revision':version,'source':url,'sourceSha256':hashlib.sha256(source).hexdigest(),'blockIndex':index,'method':method,'message':value})
p=Path('tests/Andy.MCP.Tests/Conformance/official-examples');p.mkdir(exist_ok=True)
(p/'messages.json').write_text(json.dumps(fixtures,indent=2)+'\n')
(p/'README.md').write_text(f'''# Official example messages

Extracted JSON-RPC examples from the versioned lifecycle, tools, resources and sampling
specification pages at immutable upstream commit `{sha}`. Each fixture records its source
URL, source SHA-256, zero-based JSON fence index, revision and associated request method.
Commented/non-JSON illustrative fragments are excluded. Message values are unchanged;
JSON whitespace is normalized. Upstream licensing is retained in `../schemas/LICENSE`.
These golden messages complement the generated definition corpus and malformed-message tests.
''')
print('Official examples:',len(fixtures))
