from pathlib import Path
import shutil,hashlib,json,xml.etree.ElementTree as E,sys
source=Path(sys.argv[1]).resolve();tool=Path(sys.argv[2]).resolve();root=Path(__file__).resolve().parents[1];tree=E.parse(source/'Json.csproj');mapping={};sha=lambda f:hashlib.sha256(Path(f).read_bytes()).hexdigest()
for node in tree.getroot().iter():
 if node.tag not in ['Compile','EmbeddedResource']or not node.get('Include'):continue
 old=Path(node.get('Include'));rel=old.relative_to(source)
 if rel.parts[0]=='support':
  name=rel.name
  dest=root/('src/Neemle.NativeSupport'if name.startswith('Native')else'src/System.Text.Json/Properties')/name
 else:
  parts=rel.parts[1:];prefix=('src','libraries');assert parts[:2]==prefix
  library=parts[2];tail=parts[3:]
  dest=root/('src/System.Text.Json'if library=='System.Text.Json'else 'src/Shared/'+library)/Path(*tail)
 dest.parent.mkdir(parents=True,exist_ok=True);shutil.copy2(old,dest);node.set('Include',str(Path('..')/Path('..')/dest.relative_to(root)));mapping[str(dest.relative_to(root))]={'sha256':sha(dest),'origin':str(rel)}
key=root/'eng/signing/public-key.snk';key.parent.mkdir();shutil.copy2(source/'support/public-key.snk',key);tree.getroot().find('.//AssemblyOriginatorKeyFile').text='../../eng/signing/public-key.snk';tree.getroot().find('.//Description').text='System.Text.Json source fork with inferred finite native attribute operations.';project=root/'src/System.Text.Json/System.Text.Json.csproj';tree.write(project,encoding='unicode')
for name in ['LICENSE.TXT','THIRD-PARTY-NOTICES.TXT']:shutil.copy2(source/'source'/name,root/name)
(root/'Directory.Build.props').write_text('<Project><PropertyGroup><TreatWarningsAsErrors>true</TreatWarningsAsErrors><Deterministic>true</Deterministic><ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally><UseSharedCompilation>false</UseSharedCompilation></PropertyGroup></Project>\n');(root/'Directory.Build.targets').write_text('<Project/>\n');(root/'global.json').write_text(json.dumps({'sdk':{'version':'10.0.402','rollForward':'disable','allowPrerelease':False}},indent=2));(root/'.gitignore').write_text('**/bin/\n**/obj/\n.dotnet/\n.nuget/\nartifacts/\nout/\n')
# The metadata tool has no EF assembly/package dependency. Preserve exact Tool50 source.
for folder in ['Neemle.DBAL.RuntimeContracts','Shared']:
 shutil.copytree(tool/'source'/folder,root/'tools'/folder,ignore=shutil.ignore_patterns('bin','obj'))
(root/'tools/Directory.Build.props').write_text('''<Project><PropertyGroup><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable><ImplicitUsings>enable</ImplicitUsings><LangVersion>14.0</LangVersion><TreatWarningsAsErrors>true</TreatWarningsAsErrors><ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally></PropertyGroup></Project>\n''');(root/'tools/Directory.Build.targets').write_text('<Project/>\n')
(root/'provenance/source-inventory.json').write_text(json.dumps({'upstream':{'repository':'https://github.com/dotnet/runtime','commit':'6aa72daa24f66c01c3d7b6a32519e9d21396deec','runtimeVersion':'10.0.13'},'forkBaseline':{'name':'STJ5','dllSha256':'209fabcd8d0793106666519d158d32acbc861fb775e581065b2df788983078f5','metadataToolBaseline':'Tool50','toolSha256':'bb8df103538359d847e05c3f63eb2960519096a4d66ead36a47f56c7245cb2d6'},'files':mapping,'metadataToolSources':{str(f.relative_to(root)):sha(f)for f in(root/'tools').rglob('*.cs')},'generatorSourceImportPending':True,'nativeRuntimeAbi':'Existing CF14/compiler24 +generated PropertyAccessors bridge; independent native proof pending','noExternalProjectReferences':True},indent=2));print('Imported',len(mapping),'library/resource files, exactportable metadata sources')
