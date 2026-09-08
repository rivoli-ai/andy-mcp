import json,re,copy,math
from pathlib import Path

# Run from the repository root; no network access or third-party Python modules.
root=Path('tests/Andy.MCP.Tests/Conformance/schemas')
types=set()
for p in Path('src/Andy.MCP').rglob('*.cs'):
 types.update(re.findall(r'public\s+(?:(?:sealed|abstract|readonly|partial)\s+)*(?:record(?:\s+struct)?|class|enum|struct)\s+(\w+)',p.read_text()))
aliases={
 'Annotated':'TextContent','ResourceReference':'CompletionRef','BaseMetadata':'Implementation','Icons':'Tool','ContentBlock':'Content','SamplingMessageContentBlock':'Content',
 'Request':'JsonRpcRequest','Notification':'JsonRpcNotification','Result':'PaginatedResult','EmptyResult':'PaginatedResult',
 'ClientRequest':'JsonRpcRequest','ServerRequest':'JsonRpcRequest','ClientNotification':'JsonRpcNotification','ServerNotification':'JsonRpcNotification',
 'ClientResult':'PaginatedResult','ServerResult':'PaginatedResult','Error':'JsonRpcError',
 'JSONRPCMessage':'JsonRpcMessage','JSONRPCRequest':'JsonRpcRequest','JSONRPCNotification':'JsonRpcNotification',
 'JSONRPCResponse':'JsonRpcMessage','JSONRPCResultResponse':'JsonRpcResponse','JSONRPCError':'JsonRpcMessage','JSONRPCErrorResponse':'JsonRpcMessage',
 'URLElicitationRequiredError':'JsonRpcMessage','Cursor':'String','ProgressToken':'RequestId',
 'CallToolRequestParams':'CallToolRequest','CreateMessageRequestParams':'CreateMessageRequest','InitializeRequestParams':'InitializeParams',
 'ElicitRequestParams':'ElicitRequest','ElicitRequestFormParams':'ElicitRequest','ElicitRequestURLParams':'ElicitRequest',
 'CompleteRequestParams':'CompletionRequest','CompleteResult':'CompletionResult','LoggingLevel':'McpLogLevel',
 'LoggingMessageNotificationParams':'LogMessageParams','CancelledNotificationParams':'CancelledParams','ProgressNotificationParams':'ProgressParams',
 'ResourceUpdatedNotificationParams':'ResourceUpdatedParams','ElicitationCompleteNotification':'JsonRpcNotification',
 'TaskStatusNotificationParams':'TaskStatusParams','Task':'McpTask','TaskStatus':'McpTaskStatus','GetTaskResult':'McpTask','CancelTaskResult':'McpTask',
 'GetTaskPayloadResult':'PaginatedResult','PaginatedRequestParams':'PaginatedRequest','SetLevelRequestParams':'SetLogLevelParams',
 'TaskAugmentedRequestParams':'CallToolRequest','PromptReference':'CompletionRef','ResourceTemplateReference':'CompletionRef',
 'ListToolsResult':'ToolsListResult','ListResourcesResult':'ResourcesListResult','ListResourceTemplatesResult':'ResourceTemplatesListResult','ListPromptsResult':'PromptsListResult',
 'StringSchema':'PrimitiveSchemaDefinition','NumberSchema':'PrimitiveSchemaDefinition','BooleanSchema':'PrimitiveSchemaDefinition',
 'EnumSchema':'PrimitiveSchemaDefinition','SingleSelectEnumSchema':'PrimitiveSchemaDefinition','MultiSelectEnumSchema':'PrimitiveSchemaDefinition',
 'UntitledSingleSelectEnumSchema':'PrimitiveSchemaDefinition','TitledSingleSelectEnumSchema':'PrimitiveSchemaDefinition',
 'UntitledMultiSelectEnumSchema':'PrimitiveSchemaDefinition','TitledMultiSelectEnumSchema':'PrimitiveSchemaDefinition','LegacyTitledEnumSchema':'PrimitiveSchemaDefinition',
 'ResourceContents':'TextResourceContents'
}

def model(name):
 if name in aliases:return aliases[name]
 if name.endswith('Request') or name.endswith('Notification'):return 'JsonRpcRequest' if name.endswith('Request') else 'JsonRpcNotification'
 if name in types:return name
 return None

def merge(a,b):
 if isinstance(a,dict) and isinstance(b,dict):
  out=copy.deepcopy(a)
  for k,v in b.items():out[k]=merge(out[k],v) if k in out else copy.deepcopy(v)
  return out
 return copy.deepcopy(b)

def sample(schema,defs,full=False,override=None,path='',depth=0):
 if depth>15:return None
 if schema is True or not schema:return {}
 if schema is False:raise ValueError('false schema')
 if '$ref' in schema:return sample(defs[schema['$ref'].split('/')[-1]],defs,full,override,path,depth+1)
 if 'const' in schema:return copy.deepcopy(schema['const'])
 if 'enum' in schema:
  return copy.deepcopy(schema['enum'][(override or {}).get(path+':enum',0)])
 if 'allOf' in schema:
  result={}
  for child in schema['allOf']:result=merge(result,sample(child,defs,full,override,path,depth+1))
  return result
 for key in ['anyOf','oneOf']:
  if key in schema:return sample(schema[key][(override or {}).get(path+':'+key,0)],defs,full,override,path,depth+1)
 typ=schema.get('type','object' if 'properties' in schema else None)
 if isinstance(typ,list):typ=typ[(override or {}).get(path+':type',0)]
 if typ=='null':return None
 if typ=='boolean':return True
 if typ in ['number','integer']:
  value=max(0,schema.get('minimum',0));exclusive=schema.get('exclusiveMinimum')
  if isinstance(exclusive,(int,float)) and not isinstance(exclusive,bool):value=max(value,exclusive+1)
  if 'maximum' in schema:value=min(value,schema['maximum'])
  return math.ceil(value) if typ=='integer' else value
 if typ=='string':
  value={'date-time':'2026-09-08T00:00:00Z','date':'2026-09-08','uri':'https://example.com/value','uri-reference':'https://example.com/value','uri-template':'file:///item/{id}','email':'user@example.com'}.get(schema.get('format'),'value')
  value=value.ljust(schema.get('minLength',0),'x')
  return value[:schema.get('maxLength',len(value))]
 if typ=='array':
  count=max(schema.get('minItems',0),1 if full else 0)
  count=min(count,schema.get('maxItems',count))
  return [sample(schema.get('items',{}),defs,full,override,path+'/*',depth+1) for _ in range(count)]
 if typ=='object' or typ is None:
  props=schema.get('properties',{})
  names=list(props) if full else schema.get('required',[])
  result={name:sample(props.get(name,{}),defs,full,override,path+'/'+name,depth+1) for name in names}
  return result
 raise ValueError(typ)

def choices(schema,defs,path='',seen=None):
 seen=seen or set()
 if not isinstance(schema,dict):return []
 if '$ref' in schema:
  name=schema['$ref'].split('/')[-1]
  if name in seen:return []
  return choices(defs[name],defs,path,seen|{name})
 out=[]
 for key in ['anyOf','oneOf','enum','type']:
  if isinstance(schema.get(key),list):
   for i in range(len(schema[key])):out.append({path+':'+key:i})
 for name,prop in schema.get('properties',{}).items():
  out+=choices(prop,defs,path+'/'+name,seen)
 for child in schema.get('allOf',[]):out+=choices(child,defs,path,seen)
 return out

fixtures=[];missing=[];mapping={}
for version in ['2024-11-05','2025-06-18','2025-11-25']:
 schema=json.loads((root/f'schema-{version}.json').read_text());defs=schema.get('$defs',schema.get('definitions'))
 for name,definition in defs.items():
  typ=model(name)
  if not typ:missing.append((version,name));continue
  mapping[name]=typ
  candidates=[sample(definition,defs,False),sample(definition,defs,True)]
  candidates += [sample(definition,defs,True,choice) for choice in choices(definition,defs)]
  unique=set()
  for value in candidates:
   if name=='BaseMetadata':value.setdefault('version','1')
   if name=='Annotated':value.setdefault('text','fixture')
   if name=='Icons':value.update(name='fixture',inputSchema={'type':'object'})
   if name=='ResourceContents':value.setdefault('text','fixture')
   if name=='TaskAugmentedRequestParams':value.setdefault('name','fixture')
   if name=='CancelledNotificationParams':value.setdefault('requestId',1) # Required by prose for non-task cancellation.
   if typ=='JsonRpcRequest':value.update(jsonrpc='2.0',id=1)
   if typ=='JsonRpcNotification':value.update(jsonrpc='2.0')
   if typ=='JsonRpcMessage' and isinstance(value,dict):
    if 'method' in value:value.setdefault('jsonrpc','2.0')
   if isinstance(value,dict) and definition.get('additionalProperties',True) is not False:value['vendor/fixture']={'preserved':True}
   key=json.dumps(value,sort_keys=True)
   if key in unique:continue
   unique.add(key)
   fixtures.append({'revision':version,'definition':name,'model':typ,'value':value})
if missing: raise RuntimeError(f'Unmapped definitions: {missing}')
print('Fixtures',len(fixtures),'Definitions',len(mapping))
Path('tests/Andy.MCP.Tests/Conformance/protocol-corpus/fixtures.json').write_text(json.dumps(fixtures,indent=2)+'\n')
Path('tests/Andy.MCP.Tests/Conformance/protocol-corpus/models.json').write_text(json.dumps(mapping,indent=2,sort_keys=True)+'\n')
