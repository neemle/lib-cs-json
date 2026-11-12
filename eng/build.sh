#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root"
mkdir -p .dev-temp
scratch="$root/.dev-temp/json-build-$$-$RANDOM-$RANDOM"
mkdir "$scratch"
trap 'rm -rf -- "$scratch"; rmdir "$root/.dev-temp" 2>/dev/null || true' EXIT
export TMPDIR="$scratch/tmp"
version="${PACKAGE_VERSION:-0.9.3}"
output="${PACKAGE_OUTPUT_DIR:-$root}"
mkdir -p "$output" "$scratch/nuget" "$TMPDIR"

cat > "$scratch/Neemle.SystemTextJson.targets" <<'TARGETS'
<Project>
  <PropertyGroup>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <MSBuildTreatWarningsAsErrors>true</MSBuildTreatWarningsAsErrors>
    <ILLinkTreatWarningsAsErrors>true</ILLinkTreatWarningsAsErrors>
    <IlcTreatWarningsAsErrors>true</IlcTreatWarningsAsErrors>
    <IlcResilient>false</IlcResilient>
    <JsonSerializerIsReflectionEnabledByDefault>false</JsonSerializerIsReflectionEnabledByDefault>
  </PropertyGroup>
  <ItemGroup Condition="'$(PublishAot)' == 'true'">
    <IlcArg Include="--reflectiondata:none" />
    <LinkerArg Include="-Wl,--fatal-warnings" />
  </ItemGroup>
  <Target Name="NeemleValidateWarnings" BeforeTargets="WriteIlcRspFileForCompilation"
          Condition="'$(PublishAot)' == 'true'">
    <Error Condition="!$([System.Text.RegularExpressions.Regex]::IsMatch('$(NoWarn)', '^;?((1701|1702|8002)(;|$))*$'))"
           Text="Warning suppression is not supported." />
    <PropertyGroup><NoWarn></NoWarn></PropertyGroup>
    <Error Condition="'$(IlcTreatWarningsAsErrors)' != 'true' Or '$(IlcResilient)' != 'false'"
           Text="NativeAOT warnings must be fatal." />
  </Target>
  <Target Name="NeemleRewrite" BeforeTargets="CoreCompile" DependsOnTargets="ResolveReferences"
          Condition="'$(DesignTimeBuild)' != 'true'">
    <PropertyGroup>
      <_NeemleTools>$(MSBuildThisFileDirectory)../tools</_NeemleTools>
      <_NeemleOutput>$(IntermediateOutputPath)NeemleJson/</_NeemleOutput>
    </PropertyGroup>
    <ItemGroup>
      <_NeemleInput Include="@(Compile)" />
      <_NeemleReferences Include="@(ReferencePathWithRefAssemblies)" />
    </ItemGroup>
    <Error Condition="'@(_NeemleInput->Count())' == '0' Or '@(_NeemleReferences->Count())' == '0'"
           Text="JSON generation requires source files and resolved references." />
    <RemoveDir Directories="$(_NeemleOutput)" />
    <MakeDir Directories="$(_NeemleOutput)" />
    <Error Condition="!Exists('$(MSBuildToolsPath)/Roslyn/bincore/Microsoft.CodeAnalysis.dll') Or
                      !Exists('$(MSBuildToolsPath)/Roslyn/bincore/Microsoft.CodeAnalysis.CSharp.dll')"
           Text="JSON generation requires a .NET SDK with Roslyn. SDK 10.0.403 is supported." />
    <ItemGroup>
      <_NeemleToolFiles Include="$(_NeemleTools)/JsonGenerator.dll" />
      <_NeemleToolFiles Include="$(_NeemleTools)/JsonGenerator.deps.json" />
      <_NeemleToolFiles Include="$(_NeemleTools)/JsonGenerator.runtimeconfig.json" />
      <_NeemleToolFiles Include="$(_NeemleTools)/Neemle.CSharpComposer.dll" />
      <_NeemleToolFiles Include="$(MSBuildToolsPath)/Roslyn/bincore/Microsoft.CodeAnalysis.dll" />
      <_NeemleToolFiles Include="$(MSBuildToolsPath)/Roslyn/bincore/Microsoft.CodeAnalysis.CSharp.dll" />
    </ItemGroup>
    <Copy SourceFiles="@(_NeemleToolFiles)" DestinationFolder="$(_NeemleOutput)tool/" />
    <WriteLinesToFile File="$(_NeemleOutput)sources.txt" Lines="@(_NeemleInput->'%(FullPath)')" Overwrite="true" />
    <WriteLinesToFile File="$(_NeemleOutput)references.txt" Lines="@(_NeemleReferences->'%(FullPath)')"
                      Overwrite="true" />
    <WriteLinesToFile File="$(_NeemleOutput)defines.txt" Lines="$(DefineConstants)" Overwrite="true" />
    <WriteLinesToFile File="$(_NeemleOutput)language.txt" Lines="$(LangVersion)" Overwrite="true" />
    <Exec Command="dotnet tool/JsonGenerator.dll --manifest sources.txt references.txt . defines.txt language.txt"
          WorkingDirectory="$(_NeemleOutput)" IgnoreExitCode="true">
      <Output TaskParameter="ExitCode" PropertyName="_NeemleGeneratorExitCode" />
    </Exec>
    <RemoveDir Directories="$(_NeemleOutput)tool/" />
    <Error Condition="'$(_NeemleGeneratorExitCode)' != '0'"
           Text="JSON generation failed; check the error above and use a compatible SDK (10.0.403 is supported)." />
    <ItemGroup>
      <Compile Remove="@(_NeemleInput)" />
      <_NeemleRewritten Include="$(_NeemleOutput)Source*.g.cs" />
      <Compile Include="@(_NeemleRewritten);$(_NeemleOutput)JsonCodecs*.g.cs" />
    </ItemGroup>
    <Error Condition="'@(_NeemleInput->Count())' != '@(_NeemleRewritten->Count())'"
           Text="JSON generation did not produce every source file." />
  </Target>
  <Target Name="NeemleVerifyCompilerFlags" AfterTargets="WriteIlcRspFileForCompilation"
          BeforeTargets="IlcCompile" Condition="'$(PublishAot)' == 'true'">
    <ReadLinesFromFile File="%(ManagedBinary.IlcRspFile)">
      <Output TaskParameter="Lines" ItemName="_NeemleIlcArguments" />
    </ReadLinesFromFile>
    <ItemGroup>
      <_NeemleReflection Include="@(_NeemleIlcArguments)"
        Condition="$([System.Text.RegularExpressions.Regex]::IsMatch('%(_NeemleIlcArguments.Identity)',
          '(?i)^--reflectiondata'))" />
      <_NeemleReflectionNone Include="@(_NeemleReflection)"
        Condition="'%(_NeemleReflection.Identity)' == '--reflectiondata:none'" />
      <_NeemleFatal Include="@(_NeemleIlcArguments)" Condition="'%(_NeemleIlcArguments.Identity)' == '--warnaserror'" />
      <_NeemleSuppressed Include="@(_NeemleIlcArguments)"
        Condition="$([System.Text.RegularExpressions.Regex]::IsMatch('%(_NeemleIlcArguments.Identity)',
          '(?i)^--(nowarn|nowarnaserr|notrimwarn|noaotwarn|resilient)'))" />
    </ItemGroup>
    <Error Condition="'@(_NeemleReflection->Count())' != '1' Or '@(_NeemleReflectionNone->Count())' != '1' Or
                      '@(_NeemleFatal->Count())' != '1' Or '@(_NeemleSuppressed)' != ''"
           Text="NativeAOT requires --reflectiondata:none and fatal, unsuppressed warnings." />
  </Target>
</Project>
TARGETS

cat > "$scratch/Package.csproj" <<'PROJECT'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework><PackageId>Neemle.SystemTextJson</PackageId>
    <Authors>Neemle</Authors><Description>Compile-time JSON generation for .NET NativeAOT.</Description>
    <PackageLicenseExpression>MIT</PackageLicenseExpression><PackageReadmeFile>README.md</PackageReadmeFile>
    <IncludeBuildOutput>false</IncludeBuildOutput><EnableDefaultItems>false</EnableDefaultItems>
  </PropertyGroup>
  <ItemGroup>
    <None Include="Neemle.SystemTextJson.targets" Pack="true"
          PackagePath="buildTransitive/Neemle.SystemTextJson.targets" />
    <None Include="$(JsonTool)/JsonGenerator.dll" Pack="true" PackagePath="tools/" />
    <None Include="$(JsonTool)/JsonGenerator.deps.json" Pack="true" PackagePath="tools/" />
    <None Include="$(JsonTool)/JsonGenerator.runtimeconfig.json" Pack="true" PackagePath="tools/" />
    <None Include="$(JsonTool)/Neemle.CSharpComposer.dll" Pack="true" PackagePath="tools/" />
    <None Include="$(RepositoryRoot)/README.md" Pack="true" PackagePath="README.md" />
    <None Include="_._" Pack="true" PackagePath="lib/net10.0/_._" />
  </ItemGroup>
</Project>
PROJECT
: > "$scratch/_._"

dotnet build src/JsonGenerator/JsonGenerator.csproj -c Release -o "$scratch/tool" \
  "-p:BaseIntermediateOutputPath=$scratch/generator-obj/" -warnaserror
dotnet pack "$scratch/Package.csproj" -c Release -p:PackageVersion="$version" \
  -p:JsonTool="$scratch/tool" -p:RepositoryRoot="$root" \
  "-p:BaseIntermediateOutputPath=$scratch/package-obj/" "-p:BaseOutputPath=$scratch/package-bin/" \
  -o "$output" -warnaserror

if [[ -n "${DEMO_RID:-}" ]]; then
  case "$DEMO_RID" in
    linux-musl-arm64|linux-musl-x64) ;;
    *) printf 'Unsupported DEMO_RID: %s\n' "$DEMO_RID" >&2; exit 1 ;;
  esac
  demo=samples/JsonAotDemo/JsonAotDemo.csproj
  restore_args=()
  if [[ -n "${NUGET_FALLBACK_FOLDER:-}" ]]; then
    mkdir "$scratch/fallback"
    for cached in "$NUGET_FALLBACK_FOLDER"/*; do
      [[ "${cached##*/}" == neemle.systemtextjson ]] || ln -s "$cached" "$scratch/fallback/"
    done
    restore_args+=("-p:RestoreFallbackFolders=$scratch/fallback")
  fi
  dotnet restore "$demo" -r "$DEMO_RID" -p:NeemlePackageVersion="$version" \
    "-p:BaseIntermediateOutputPath=$scratch/demo-obj/" "-p:BaseOutputPath=$scratch/demo-bin/" \
    --packages "$scratch/nuget" --source "$output" --source https://api.nuget.org/v3/index.json "${restore_args[@]}"
  dotnet publish "$demo" --no-restore -c Release -r "$DEMO_RID" -p:NeemlePackageVersion="$version" \
    "-p:BaseIntermediateOutputPath=$scratch/demo-obj/" "-p:BaseOutputPath=$scratch/demo-bin/" -p:NoWarn= -warnaserror
  "$scratch/demo-bin/Release/net10.0/$DEMO_RID/publish/JsonAotDemo"
fi

if [[ -n "${NUGET_SOURCE:-}" || -n "${NUGET_API_KEY:-}" ]]; then
  : "${NUGET_SOURCE:?Set NUGET_SOURCE to publish the package}"
  : "${NUGET_API_KEY:?Set NUGET_API_KEY to publish the package}"
  dotnet nuget push "$output/Neemle.SystemTextJson.$version.nupkg" --source "$NUGET_SOURCE" --api-key "$NUGET_API_KEY"
fi
