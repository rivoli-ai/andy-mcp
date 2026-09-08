# Package and runtime maintenance

The approved migration in PR91 dropped .NET 8 on 2026-07-31; both packages target .NET 10 only.
Consumers must migrate their host to .NET 10. The SDK policy uses a stable 10.0 minimum and
rolls forward to installed feature bands; CI installs the latest patched 10.0 SDK.

Dependency versions are central in Directory.Packages.props. NuGet.Config selects nuget.org
explicitly, and each project commits packages.lock.json. CI restores with --locked-mode.
To update, change the central version, run dotnet restore --force-evaluate, review every lock
change, and run the full tests, examples, security and package-content checks before merging.
The security job reports outdated dependencies and rejects known vulnerabilities.

JsonSchema.Net remains on the compatible 7.3 line while the 9.x build/evaluation API migration
is evaluated separately. Its major version is not silently changed as part of dependency refresh.

Package validation is enabled in the SDK. scripts/verify-packages.py checks both packages'
assemblies, documentation, symbols, README, icon, repository and dependency groups. Release
publication depends on all tests, interop, security and these package checks.

Reflection-based serialization and attribute discovery are not Native AOT or trimming certified.
Attribute registration APIs explicitly declare RequiresUnreferencedCode and RequiresDynamicCode.
Do not trim registered model/handler types. General protocol serializers still use reflection;
source-generated metadata and a Native AOT smoke application are required before claiming support.

2026-09-08: updated .NET/test dependencies, removed obsolete transitive test pins, added locked
restore and package-content gates, and fixed two previously unawaited test assertions.

Public API migration compatibility is checked against pre-migration commit 82cb2dba with
Microsoft.DotNet.ApiCompat.Tool 10.0.400 by scripts/verify-api.sh in the release gate. Both
library comparisons pass without breaking API changes; this does not restore .NET 8 runtime support.
