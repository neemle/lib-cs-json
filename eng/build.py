#!/usr/bin/env python3
"""Build the actual library and generator with the selected stable .NET 10 SDK."""
from pathlib import Path
import os,subprocess,sys,json,hashlib
root=Path(__file__).resolve().parents[1];dotnet=Path(os.environ.get('DOTNET_ROOT',str(root/'.dotnet')))/'dotnet';dotnet=str(dotnet)if dotnet.exists()else'dotnet';out=root/'artifacts';out.mkdir(exist_ok=True);env=os.environ|{'DOTNET_PROCESSOR_COUNT':'2','NUGET_PACKAGES':str(root/'.nuget/packages')};rows=[]
for name,project in [('library','src/System.Text.Json/System.Text.Json.csproj'),('generator','src/System.Text.Json.SourceGeneration/System.Text.Json.SourceGeneration.csproj'),('metadata','tools/Neemle.DBAL.RuntimeContracts/Neemle.DBAL.RuntimeContracts.csproj'),('sourcegen-consumer','tests/SourceGenerated/SourceGenerated.csproj')]:
 cmd=[dotnet,'build',project,'-c','Release',f'-bl:{out}/{name}-build.binlog'];log=out/(name+'-build.log')
 with log.open('w')as stream:q=subprocess.run(cmd,cwd=root,env=env,stdout=stream,stderr=subprocess.STDOUT)
 rows.append({'name':name,'project':project,'exitCode':q.returncode});print(name,q.returncode,flush=True)
 if q.returncode:(out/'build-result.json').write_text(json.dumps({'passed':False,'builds':rows},indent=2));sys.exit(q.returncode)
(out/'build-result.json').write_text(json.dumps({'passed':True,'builds':rows,'nativeCompatibilityEstablished':False},indent=2))
