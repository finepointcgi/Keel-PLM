# Third-party licences

Every third-party component that Keel PLM delivers, and every one it uses to build and test, with its licence and a hash. **This file is generated; do not edit it.** Regenerate with `scripts/generate-sbom.sh --licenses` after changing any dependency (NuGet, the viewer's npm lock file, a vendored file, an embedded font, an image pin, the Python add-on's requirements). `ThirdPartyLicensesTests` fails when a dependency has no row here or a row is stale, and `keel-sbom licenses --check` runs the same check in CI (ADR-0059).

It is an engineering inventory read from the repository's own files (project assets, lock files, manifests, nuspec metadata). It is not legal advice and nothing here says counsel has approved a licence conclusion: the legal packet is [docs/review/legal-open-source.md](docs/review/legal-open-source.md) and its status is pending. Texts of the licences that ship with the delivered components are in the offline bundle's `THIRD-PARTY-NOTICES.md` and in [docs/third-party/viewer-licenses](docs/third-party/viewer-licenses). The full SBOM (CycloneDX 1.5, SHA-256 and SHA-512 hashes) is produced by `scripts/generate-sbom.sh`.

Hash column: SHA-256 of the file for files; for NuGet and npm packages the first 32 hex characters of the registry's SHA-512 of the package archive (the SBOM carries all 128); the digest of the manifest for container images. A dash means the source gives none.

## 1. Delivered with the product (36)

NuGet packages of `Keel.Api`, `Keel.AuditCli` and `Keel.VaultTool` and everything they pull in (for example MudBlazor, PdfPig, PDFsharp, AWSSDK.S3, Npgsql), the vendored browser files (three.js, pdf.js, occt-import-js with OpenCascade), the npm packages those files came from, the embedded DejaVu fonts, and the components compiled into the wasm file.

| Component | Version | Licence | Kind | Hash |
|---|---|---|---|---|
| Emscripten runtime libraries (Emscripten 3.1.69) | 3.1.69 | MIT AND Apache-2.0 WITH LLVM-exception | bundled | - |
| OpenCascade Technology (OCCT) | d2abb6d844231cb8f29be6894440874a4700e4a5 | LGPL-2.1-only WITH OCCT-exception-1.0 | bundled | - |
| Keel.Documents/Marking/Fonts/DejaVuSans-Bold.ttf |  | Bitstream-Vera | embedded-file | sha256:e6476c1b80502924294eed40894c5b18e06c181444ca953e5334262df9c27724 |
| Keel.Documents/Marking/Fonts/DejaVuSans.ttf |  | Bitstream-Vera | embedded-file | sha256:7da195a74c55bef988d0d48f9508bd5d849425c1770dba5d7bfc6ce9ed848954 |
| occt-import-js | 0.0.23 | LGPL-2.1 | npm | sha512:4457d84176055f90b5981d40cb09b44a |
| pdfjs-dist | 4.10.38 | Apache-2.0 | npm | sha512:fd8ddf705ad7100b0c8c95de2fd27cf9 |
| three | 0.186.1 | MIT | npm | sha512:6e515ea9be3dc110921941a3ee0b697e |
| AWSSDK.Core | 4.0.102.8 | Apache-2.0 | nuget | sha512:f7407d4a4865f8e45bb56357aeb98674 |
| AWSSDK.S3 | 4.0.104 | Apache-2.0 | nuget | sha512:6b31d299057dc0f7cf232ed6bda88722 |
| Microsoft.AspNetCore.App.Internal.Assets | 10.0.13 | MIT | nuget | sha512:6367179cc9ea86de5fbe36b61fc924af |
| Microsoft.AspNetCore.Authentication.JwtBearer | 10.0.14 | MIT | nuget | sha512:0da4ecdf636d4644da6e276bbe12e171 |
| Microsoft.AspNetCore.Authentication.OpenIdConnect | 10.0.14 | MIT | nuget | sha512:5b983dc5a5a33ee770ec6370c42a8ff6 |
| Microsoft.AspNetCore.OpenApi | 10.0.14 | MIT | nuget | sha512:dcceb23b89eff1f9af33fd57ec3a9070 |
| Microsoft.Bcl.Cryptography | 10.0.2 | MIT | nuget | sha512:2c6f58965dc1e5a369c6fd3e0f8ee0e8 |
| Microsoft.Extensions.DependencyInjection.Abstractions | 10.0.0 | MIT | nuget | sha512:2f701d999d5638ae175d3e5814f130ca |
| Microsoft.Extensions.Logging.Abstractions | 10.0.0 | MIT | nuget | sha512:154fc87e30dfc1a32e2abe35e1249035 |
| Microsoft.IdentityModel.Abstractions | 8.19.2 | MIT | nuget | sha512:1c96e8fe59d27cd1d47e984f453f75d2 |
| Microsoft.IdentityModel.JsonWebTokens | 8.19.2 | MIT | nuget | sha512:ba2ddfb814f87ecf2474a7c1b6123837 |
| Microsoft.IdentityModel.Logging | 8.19.2 | MIT | nuget | sha512:af960b0c8c463a7915247aea5effe20f |
| Microsoft.IdentityModel.Protocols | 8.19.2 | MIT | nuget | sha512:b06c52b12ad95cd99c6ba0fe8c71f6ad |
| Microsoft.IdentityModel.Protocols.OpenIdConnect | 8.19.2 | MIT | nuget | sha512:d5739cc98dfa715ca6cc4dea29dcca69 |
| Microsoft.IdentityModel.Tokens | 8.19.2 | MIT | nuget | sha512:1ad3c2d52d36b87d6038ee1f43ecd1ca |
| Microsoft.OpenApi | 2.12.2 | MIT | nuget | sha512:6644fbaa225ed0a8f4f834d9399a3ec6 |
| MudBlazor | 9.12.0 | MIT | nuget | sha512:4fa3d050225eeb45134b2deed45f11da |
| Npgsql | 10.0.3 | PostgreSQL | nuget | sha512:ee76f96335eebd6589c41d09f038b22f |
| PdfPig | 0.1.16 | Apache-2.0 | nuget | sha512:a9071bb0d318558f8ae442e9b5ac764b |
| PDFsharp | 6.2.4 | MIT | nuget | sha512:1a14ebf45d6e3985c708810e0793d722 |
| prometheus-net | 8.2.1 | MIT | nuget | sha512:df05607443ce08117be76b36c69b394f |
| prometheus-net.AspNetCore | 8.2.1 | MIT | nuget | sha512:ff84df4ef6f0203aa968a4e25af12c8d |
| System.IdentityModel.Tokens.Jwt | 8.19.2 | MIT | nuget | sha512:82a8430bf89c072284badca0a6bf8e16 |
| System.Security.Cryptography.Pkcs | 10.0.14 | MIT | nuget | sha512:5b3f8880bdb1a60f0a2788b65568fabb |
| viewer/vendor/occt-import-js.js |  | LGPL-2.1 | vendored-file | sha256:3fb44ce11d00611f9b3f3c5775d520ebab48930c1f08279b7b1316f05f0d3379 |
| viewer/vendor/occt-import-js.wasm |  | LGPL-2.1 | vendored-file | sha256:33391fc9d94ea5c869a6718488bf0a9a464222bac9bdc764dfe1690cef281952 |
| viewer/vendor/pdf.min.mjs |  | Apache-2.0 | vendored-file | sha256:27fc2a057a00f92a4334ad06e17dbd7259912954e9fb7f76400bcca5fd190a9c |
| viewer/vendor/pdf.worker.min.mjs |  | Apache-2.0 | vendored-file | sha256:1baa1844c89c80a5b2797c916e75ab29254be46d8e9cb53cb6364d7aad84be36 |
| viewer/vendor/three-bundle.js |  | MIT | vendored-file | sha256:1b5e810aa9149dcccff96fd1a4246290f3753aad64b88ce10236a1a65b09f99e |

## 2. Used to build, test and maintain, not delivered (163)

Test and tooling NuGet packages, the viewer vendoring tool's build dependencies, the Python packages of the add-on's tests and fixture generator, the FreeCAD host application, and the test-only axe-core. The SBOM marks them with scope `excluded`.

| Component | Version | Licence | Kind | Hash |
|---|---|---|---|---|
| FreeCAD |  | LGPL-2.0-or-later | host-application | - |
| PySide |  | LGPL-3.0-only | host-application | - |
| @esbuild/aix-ppc64 | 0.25.12 | MIT | npm | sha512:1e19b077a0889d9dddc29b864c5f1f24 |
| @esbuild/android-arm | 0.25.12 | MIT | npm | sha512:549fac2af340fc613b09c69c73d0a16b |
| @esbuild/android-arm64 | 0.25.12 | MIT | npm | sha512:e800262c6ef3c03d59d79f6308a3ef03 |
| @esbuild/android-x64 | 0.25.12 | MIT | npm | sha512:e636dbfb68610c7c79a61611d81cbc19 |
| @esbuild/darwin-arm64 | 0.25.12 | MIT | npm | sha512:377ce5fa5c470a27e022570c50fe74d7 |
| @esbuild/darwin-x64 | 0.25.12 | MIT | npm | sha512:1d0f646b82b1db5a875f0b654d455b28 |
| @esbuild/freebsd-arm64 | 0.25.12 | MIT | npm | sha512:800d01c7be7dfbb26f7b4dcad52d2f90 |
| @esbuild/freebsd-x64 | 0.25.12 | MIT | npm | sha512:4c66cedba630db1b07cf1b5b5451845c |
| @esbuild/linux-arm | 0.25.12 | MIT | npm | sha512:94f0c6c82d493c3a2ef2419ccb063460 |
| @esbuild/linux-arm64 | 0.25.12 | MIT | npm | sha512:f1bc17edaf05821220aeea5cc5be1a52 |
| @esbuild/linux-ia32 | 0.25.12 | MIT | npm | sha512:d32f4aadd5676cc336fef1bc29f5346f |
| @esbuild/linux-loong64 | 0.25.12 | MIT | npm | sha512:87ffff2ebe5af6b89bfefd461aa5d51b |
| @esbuild/linux-mips64el | 0.25.12 | MIT | npm | sha512:8b246b3353f3cbd1853032ec5e7d621d |
| @esbuild/linux-ppc64 | 0.25.12 | MIT | npm | sha512:f6678cfe5457c4c8b93d252a117442b5 |
| @esbuild/linux-riscv64 | 0.25.12 | MIT | npm | sha512:66beca478860294a560306f57f7a39ca |
| @esbuild/linux-s390x | 0.25.12 | MIT | npm | sha512:32c2a770e7204cdbddb6221273f8d9b3 |
| @esbuild/linux-x64 | 0.25.12 | MIT | npm | sha512:baa64c4cbaffcd1fde7788c81a7c122e |
| @esbuild/netbsd-arm64 | 0.25.12 | MIT | npm | sha512:c57c1c4eae0685133b27d03c1afe5ba1 |
| @esbuild/netbsd-x64 | 0.25.12 | MIT | npm | sha512:2dde694e5ccfcb763019e7383ae1e1d5 |
| @esbuild/openbsd-arm64 | 0.25.12 | MIT | npm | sha512:7c5f7a4fa2ac068fe991023de7414045 |
| @esbuild/openbsd-x64 | 0.25.12 | MIT | npm | sha512:319c975246478d0c54bf32bbacdf0327 |
| @esbuild/openharmony-arm64 | 0.25.12 | MIT | npm | sha512:ae6d185aca94491ae39dc497180ed9bf |
| @esbuild/sunos-x64 | 0.25.12 | MIT | npm | sha512:df0192083cae4c7414cedd2757b6e870 |
| @esbuild/win32-arm64 | 0.25.12 | MIT | npm | sha512:acc98baeeafae00efe0ca9674aec2a51 |
| @esbuild/win32-ia32 | 0.25.12 | MIT | npm | sha512:1e4aa79a606809b0b0c5428a34f062c6 |
| @esbuild/win32-x64 | 0.25.12 | MIT | npm | sha512:6a5242d2e099a5316b48bd020838dc82 |
| @napi-rs/canvas | 0.1.100 | MIT | npm | sha512:c6095803aab75cee4fdc1349631559d4 |
| @napi-rs/canvas-android-arm64 | 0.1.100 | MIT | npm | sha512:8638422a19ed3eff7eb787241f299dc7 |
| @napi-rs/canvas-darwin-arm64 | 0.1.100 | MIT | npm | sha512:d8f72cc11682ecbcbae390c6b7cf3fff |
| @napi-rs/canvas-darwin-x64 | 0.1.100 | MIT | npm | sha512:78f359b63ee9348bdafec899320f8799 |
| @napi-rs/canvas-linux-arm-gnueabihf | 0.1.100 | MIT | npm | sha512:779703078f28585194f3f5cf85438502 |
| @napi-rs/canvas-linux-arm64-gnu | 0.1.100 | MIT | npm | sha512:ac3c60c51bbaf51bc3957fdb87da36d8 |
| @napi-rs/canvas-linux-arm64-musl | 0.1.100 | MIT | npm | sha512:2b79835bae8dfb14f6fd5e37f6ed5a94 |
| @napi-rs/canvas-linux-riscv64-gnu | 0.1.100 | MIT | npm | sha512:9a8a2a5014c8b1c719a67a100b8360ac |
| @napi-rs/canvas-linux-x64-gnu | 0.1.100 | MIT | npm | sha512:d5e0af90309ace6ec5161b13ec37c639 |
| @napi-rs/canvas-linux-x64-musl | 0.1.100 | MIT | npm | sha512:db46ab4fa967235f52ebca8d9628bbdd |
| @napi-rs/canvas-win32-arm64-msvc | 0.1.100 | MIT | npm | sha512:0d91454f5c08020dfb2c9c37ef284c44 |
| @napi-rs/canvas-win32-x64-msvc | 0.1.100 | MIT | npm | sha512:3324f58f79870b6f8bbb8a418bd98ac8 |
| esbuild | 0.25.12 | MIT | npm | sha512:6db3c1618aed65b92de8eb3a1624cb09 |
| AngleSharp | 1.8.6 | MIT | nuget | sha512:b5dbeb592b07cfc89171bf14d9c6c5b5 |
| AngleSharp.Css | 1.2.0 | MIT | nuget | sha512:c7cff2aca245d247d574d5b5e764989f |
| AngleSharp.Diffing | 1.1.1 | MIT | nuget | sha512:533cf9f3de53a51c8c038b7c184c66ab |
| BouncyCastle.Cryptography | 2.7.0 | MIT | nuget | sha512:53ed7675ff141161e0062d386157ff2e |
| bunit | 2.13.3 | MIT | nuget | sha512:0b3177d0a218a8b4c174a382c9d18707 |
| Docker.DotNet.Enhanced | 4.3.3 | MIT | nuget | sha512:9c689c2f0bdde36161464fa4858e6e4b |
| Docker.DotNet.Enhanced.Handler.Abstractions | 4.3.3 | MIT | nuget | sha512:f42a7c84e82dca78b1703a00b3d96711 |
| Docker.DotNet.Enhanced.LegacyHttp | 4.3.3 | MIT | nuget | sha512:ee3dccd7a7a6bfd3c040ded5c059f6df |
| Docker.DotNet.Enhanced.NativeHttp | 4.3.3 | MIT | nuget | sha512:88dccafa34457843286d203b97f87801 |
| Docker.DotNet.Enhanced.NPipe | 4.3.3 | MIT | nuget | sha512:6532d8b9fb847d8d1eeaa2ce81e1fd42 |
| Docker.DotNet.Enhanced.Unix | 4.3.3 | MIT | nuget | sha512:ca9a3ca8d6e6bc7c35b7d55fa514cca2 |
| Docker.DotNet.Enhanced.X509 | 4.3.3 | MIT | nuget | sha512:a010e26d67b312fe21823dd1210c48dc |
| Microsoft.ApplicationInsights | 2.23.0 | MIT | nuget | sha512:9d602b5194dd53b8aa64bc9c2ca59ed1 |
| Microsoft.AspNetCore.Authorization | 10.0.1 | MIT | nuget | sha512:63d404d201f8438711ed9453a058eee3 |
| Microsoft.AspNetCore.Authorization | 10.0.13 | MIT | nuget | sha512:32b041097fc667457e8eebb202181fb6 |
| Microsoft.AspNetCore.Components | 10.0.1 | MIT | nuget | sha512:49b001710eeca0cf630bf1ef3caae0fe |
| Microsoft.AspNetCore.Components | 10.0.13 | MIT | nuget | sha512:01914dcb05d778afa3a2abbfce303874 |
| Microsoft.AspNetCore.Components.Analyzers | 10.0.1 | MIT | nuget | sha512:5789ceab498da17f4e5b3f69273e2e2d |
| Microsoft.AspNetCore.Components.Analyzers | 10.0.13 | MIT | nuget | sha512:a7bd1bd764dd08ec2e403ef822048fd5 |
| Microsoft.AspNetCore.Components.Authorization | 10.0.13 | MIT | nuget | sha512:70811ce84ab1d9feb0e305efb8405ace |
| Microsoft.AspNetCore.Components.Forms | 10.0.1 | MIT | nuget | sha512:6965292cecfbe3d83030c68a7bcd6d7a |
| Microsoft.AspNetCore.Components.Forms | 10.0.13 | MIT | nuget | sha512:899a779448a9fa27ca01df7cc7c5aaf4 |
| Microsoft.AspNetCore.Components.Web | 10.0.1 | MIT | nuget | sha512:857ef88a3a808947c8a3a5a9bcb5a282 |
| Microsoft.AspNetCore.Components.Web | 10.0.13 | MIT | nuget | sha512:0073f1740facdd6df7ff495680c87306 |
| Microsoft.AspNetCore.Components.WebAssembly | 10.0.13 | MIT | nuget | sha512:c375768a3e16a6d0d738b2eeefca37fd |
| Microsoft.AspNetCore.Components.WebAssembly.Authentication | 10.0.13 | MIT | nuget | sha512:cd2f8cafde5e782e4d8d505ac2538859 |
| Microsoft.AspNetCore.Metadata | 10.0.1 | MIT | nuget | sha512:e89ac6d37c513ae47899020719c4fc3a |
| Microsoft.AspNetCore.Metadata | 10.0.13 | MIT | nuget | sha512:e49ca8cb2252e06b7a31b47c9894ab6b |
| Microsoft.AspNetCore.Mvc.Testing | 10.0.14 | MIT | nuget | sha512:977785c9ee01bf8a064f5020f2916d67 |
| Microsoft.AspNetCore.TestHost | 10.0.14 | MIT | nuget | sha512:bb8017d4ce54ce6c3b57734274924ee0 |
| Microsoft.Bcl.AsyncInterfaces | 6.0.0 | MIT | nuget | sha512:51c4a33ecb2df837c07491950ecbb7e3 |
| Microsoft.Extensions.Caching.Abstractions | 10.0.13 | MIT | nuget | sha512:50e26a71d34ca05715ff945470e9552a |
| Microsoft.Extensions.Caching.Memory | 10.0.13 | MIT | nuget | sha512:abe02deb94400ae0ad1b0503548d3c4e |
| Microsoft.Extensions.Configuration | 10.0.13 | MIT | nuget | sha512:8a786e8d823f9e292198bc5823a64471 |
| Microsoft.Extensions.Configuration | 10.0.14 | MIT | nuget | sha512:51c9ef68468cbd17240c3872a98d4e66 |
| Microsoft.Extensions.Configuration | 3.1.0 | Apache-2.0 | nuget | sha512:2eee35056366c212abe8b83242f71804 |
| Microsoft.Extensions.Configuration.Abstractions | 10.0.13 | MIT | nuget | sha512:0d8e02daacd3b7694e218dd5a2c7011f |
| Microsoft.Extensions.Configuration.Abstractions | 10.0.14 | MIT | nuget | sha512:e1875fbdaa169f7b41d714101c6ae145 |
| Microsoft.Extensions.Configuration.Abstractions | 3.1.0 | Apache-2.0 | nuget | sha512:112cfa6d5a03417eec81674a1c5e86f4 |
| Microsoft.Extensions.Configuration.Binder | 10.0.13 | MIT | nuget | sha512:c89265fdbd3e5be18459bbbeeef8b0e8 |
| Microsoft.Extensions.Configuration.Binder | 10.0.14 | MIT | nuget | sha512:6eb0dbe648257b6898da558781e57f28 |
| Microsoft.Extensions.Configuration.Binder | 3.1.0 | Apache-2.0 | nuget | sha512:a3d7842c305f36447bb14b58cac159d5 |
| Microsoft.Extensions.Configuration.CommandLine | 10.0.14 | MIT | nuget | sha512:ffd22a901781ee61357a76cde027debb |
| Microsoft.Extensions.Configuration.EnvironmentVariables | 10.0.14 | MIT | nuget | sha512:8c1d148988e99e77a8047d597973e51d |
| Microsoft.Extensions.Configuration.FileExtensions | 10.0.13 | MIT | nuget | sha512:9300b50d8e790aca8f983b49033fbb6a |
| Microsoft.Extensions.Configuration.FileExtensions | 10.0.14 | MIT | nuget | sha512:34121f9bcbd6362dd427d388f6d86f44 |
| Microsoft.Extensions.Configuration.Json | 10.0.13 | MIT | nuget | sha512:69c096336f9647aee4040cfa800843db |
| Microsoft.Extensions.Configuration.Json | 10.0.14 | MIT | nuget | sha512:e139fc78d83ce71f8808337838b0bb0e |
| Microsoft.Extensions.Configuration.UserSecrets | 10.0.14 | MIT | nuget | sha512:cfe76f97db909cdcefaba5dca3e510c9 |
| Microsoft.Extensions.DependencyInjection | 10.0.13 | MIT | nuget | sha512:e2d9d653dbeb53f03c37061ef59eb0be |
| Microsoft.Extensions.DependencyInjection | 10.0.14 | MIT | nuget | sha512:d8bcdcb958de99e5d1f96758f70188af |
| Microsoft.Extensions.DependencyInjection | 3.1.0 | Apache-2.0 | nuget | sha512:29592fdda176310a66185461e31471d8 |
| Microsoft.Extensions.DependencyInjection.Abstractions | 10.0.13 | MIT | nuget | sha512:1b61ab1963403d906c8a2d85e8053c9f |
| Microsoft.Extensions.DependencyInjection.Abstractions | 10.0.14 | MIT | nuget | sha512:42da50cee4995724cf0ae1d6d39f320a |
| Microsoft.Extensions.DependencyModel | 10.0.14 | MIT | nuget | sha512:37c39ca8cd37ef4c125cc2776713baea |
| Microsoft.Extensions.Diagnostics | 10.0.13 | MIT | nuget | sha512:27d3833ab7df8f598adc4b7af5e94db0 |
| Microsoft.Extensions.Diagnostics | 10.0.14 | MIT | nuget | sha512:f3456a5bc78ac506b104e2c33692d477 |
| Microsoft.Extensions.Diagnostics.Abstractions | 10.0.13 | MIT | nuget | sha512:a19cea6737bc79dd5f2d2db41ed22b90 |
| Microsoft.Extensions.Diagnostics.Abstractions | 10.0.14 | MIT | nuget | sha512:be6f8f3eca3abcec06797e7631d38f87 |
| Microsoft.Extensions.FileProviders.Abstractions | 10.0.13 | MIT | nuget | sha512:0590f0b4ddeea164608438b99514593c |
| Microsoft.Extensions.FileProviders.Abstractions | 10.0.14 | MIT | nuget | sha512:ac0a9fd849b66632ea35b70b2656e842 |
| Microsoft.Extensions.FileProviders.Physical | 10.0.13 | MIT | nuget | sha512:53b1e480608d3c808de64ecd54ae9d98 |
| Microsoft.Extensions.FileProviders.Physical | 10.0.14 | MIT | nuget | sha512:75ca7a782224e99a44dc24cd9889dad2 |
| Microsoft.Extensions.FileSystemGlobbing | 10.0.13 | MIT | nuget | sha512:bd05739806e7ce585749703671a62797 |
| Microsoft.Extensions.FileSystemGlobbing | 10.0.14 | MIT | nuget | sha512:c6de9cfcf58b2faf828818990373d645 |
| Microsoft.Extensions.Hosting | 10.0.14 | MIT | nuget | sha512:ab77f618b65e6edc561bfa43b5c90fae |
| Microsoft.Extensions.Hosting.Abstractions | 10.0.14 | MIT | nuget | sha512:a7a3541249a5681f85ba34194edc8d04 |
| Microsoft.Extensions.Http | 3.1.0 | Apache-2.0 | nuget | sha512:0cb8a075c5749d8693ebf972d2b9df3f |
| Microsoft.Extensions.Localization | 10.0.1 | MIT | nuget | sha512:c89048dc8466f6e4d1bbb70f73b8bdd3 |
| Microsoft.Extensions.Localization.Abstractions | 10.0.1 | MIT | nuget | sha512:4d0490585fa265db463603e2bbb80a52 |
| Microsoft.Extensions.Localization.Abstractions | 10.0.13 | MIT | nuget | sha512:7fdfde239b18346683ca77a0e7cd03e2 |
| Microsoft.Extensions.Logging | 10.0.13 | MIT | nuget | sha512:7ea6e23f2e27529e07d971ca01541728 |
| Microsoft.Extensions.Logging | 10.0.14 | MIT | nuget | sha512:3b67d72fa266b641709228314bc7cf4d |
| Microsoft.Extensions.Logging | 3.1.0 | Apache-2.0 | nuget | sha512:3fef2c290f0be28a0befdb31c6ac053f |
| Microsoft.Extensions.Logging.Abstractions | 10.0.13 | MIT | nuget | sha512:6926448448a0e1fbcd8b0db3b3c67a37 |
| Microsoft.Extensions.Logging.Abstractions | 10.0.14 | MIT | nuget | sha512:4b071b101b64102ab70f6a32c2f1e49b |
| Microsoft.Extensions.Logging.Configuration | 10.0.14 | MIT | nuget | sha512:09f15235b5525d297c5131f8e777341e |
| Microsoft.Extensions.Logging.Console | 10.0.14 | MIT | nuget | sha512:f10c79ee9dec5006a34991cb41ff1e8a |
| Microsoft.Extensions.Logging.Debug | 10.0.14 | MIT | nuget | sha512:f1eefdba89d7ce022c65b760739a5ad9 |
| Microsoft.Extensions.Logging.EventLog | 10.0.14 | MIT | nuget | sha512:e076d66b242225dc34958b610b8e6b12 |
| Microsoft.Extensions.Logging.EventSource | 10.0.14 | MIT | nuget | sha512:434e90f740f7aa7aec2ad7f542568c10 |
| Microsoft.Extensions.ObjectPool | 7.0.0 | MIT | nuget | sha512:b9dbca728d2c0158181930671d46f4b5 |
| Microsoft.Extensions.Options | 10.0.13 | MIT | nuget | sha512:b6630981dac4e2e85fd4970db3d095f6 |
| Microsoft.Extensions.Options | 10.0.14 | MIT | nuget | sha512:11e37fdf74b8d7c9bda1611a658e0de5 |
| Microsoft.Extensions.Options | 3.1.0 | Apache-2.0 | nuget | sha512:f5be891d8ed3017ad27d9e841067fe8f |
| Microsoft.Extensions.Options.ConfigurationExtensions | 10.0.13 | MIT | nuget | sha512:c553b91e1013734f565ee4bf45a0e6c7 |
| Microsoft.Extensions.Options.ConfigurationExtensions | 10.0.14 | MIT | nuget | sha512:e30708534ac4d387a311f0b01d37edbb |
| Microsoft.Extensions.Primitives | 10.0.13 | MIT | nuget | sha512:6634f0c73862c364cc13321b50390c17 |
| Microsoft.Extensions.Primitives | 10.0.14 | MIT | nuget | sha512:d5b6d9aaa0894cf6dade29d30031316e |
| Microsoft.Extensions.Primitives | 3.1.0 | Apache-2.0 | nuget | sha512:2c42809d7ee5854852a087365eb6824c |
| Microsoft.Extensions.Validation | 10.0.1 | MIT | nuget | sha512:e5b72ef735a1818f00654375111347d0 |
| Microsoft.Extensions.Validation | 10.0.13 | MIT | nuget | sha512:df3cf2674a195ad239a8816ff1b9c773 |
| Microsoft.JSInterop | 10.0.1 | MIT | nuget | sha512:a537e86018ecec72a64c493d70d59c4b |
| Microsoft.JSInterop | 10.0.13 | MIT | nuget | sha512:04c79ea323501c6582ff52b14ef08280 |
| Microsoft.JSInterop.WebAssembly | 10.0.13 | MIT | nuget | sha512:921ebc450dca9a0292b08a6441eb5bb8 |
| Microsoft.Playwright | 1.61.0 | MIT | nuget | sha512:54cd78f6b4363eefb7c40ceb3b682f87 |
| Microsoft.Testing.Extensions.Telemetry | 2.5.1 | MIT | nuget | sha512:9aeda2ca81e8bf0974ab598679e454d2 |
| Microsoft.Testing.Extensions.TrxReport.Abstractions | 2.5.1 | MIT | nuget | sha512:0d84d9020b9a1f3d303497470d34c660 |
| Microsoft.Testing.Platform | 2.5.1 | MIT | nuget | sha512:14031a1455a750b5be8431cc6f8d10ce |
| Microsoft.Testing.Platform.MSBuild | 2.5.1 | MIT | nuget | sha512:9586b0700c18fa1efaad09a999ada7d0 |
| Microsoft.Win32.Registry | 5.0.0 | MIT | nuget | sha512:743a0a8b43e70f3df5c80c844df467b6 |
| SharpZipLib | 1.4.2 | MIT | nuget | sha512:ca38fedf3833f33817a62882dd9745fe |
| SSH.NET | 2026.0.0 | MIT | nuget | sha512:62ef5d8ab3eaf25de869ab74f8e43b2b |
| System.ComponentModel.Annotations | 5.0.0 | MIT | nuget | sha512:74c92a7f2d9e97c03cfc8efa9f61e2d6 |
| System.Diagnostics.EventLog | 10.0.14 | MIT | nuget | sha512:3bf4733705a80f37a190b9401bd851f3 |
| System.Security.AccessControl | 6.0.1 | MIT | nuget | sha512:210e0d5cffc1dc0cb3bf0d2b0d0cd54d |
| Testcontainers | 4.16.0 | MIT | nuget | sha512:266314664e995d0a2f6c642368c07e63 |
| Testcontainers.PostgreSql | 4.16.0 | MIT | nuget | sha512:42abc54bc3a3fa2e9d787dd024e31dee |
| xunit.analyzers | 2.2.0 | Apache-2.0 | nuget | sha512:757308233b62a5c0322f76b5f60b54ae |
| xunit.v3 | 4.0.2 | Apache-2.0 | nuget | sha512:4dd8ccd8e031ef0ff413cf04e8f81a8a |
| xunit.v3.assert | 4.0.2 | Apache-2.0 | nuget | sha512:e4f2917b1625bee7d615d5de9d171118 |
| xunit.v3.common | 4.0.2 | Apache-2.0 | nuget | sha512:fbe0ad4fc75a21feaa7e27907afffc57 |
| xunit.v3.core.mtp-v2 | 4.0.2 | Apache-2.0 | nuget | sha512:5f23cc71046c9d440b8660414518185a |
| xunit.v3.extensibility.core | 4.0.2 | Apache-2.0 | nuget | sha512:0114edfa91b277cea676d8850642a9c8 |
| xunit.v3.mtp-v2 | 4.0.2 | Apache-2.0 | nuget | sha512:5cb577d85864baf85e052b6f5f40ef8d |
| xunit.v3.runner.common | 4.0.2 | Apache-2.0 | nuget | sha512:62560a5614c955cc04809eab9b9f4f8d |
| xunit.v3.runner.inproc.console | 4.0.2 | Apache-2.0 | nuget | sha512:720e1736908de11cbd9ecc2eb649e9ad |
| cadquery | unpinned | Apache-2.0 | python | - |
| jsonschema | >=4 | MIT | python | - |
| OCP | unpinned | Apache-2.0 | python | - |
| pytest | >=8 | MIT | python | - |
| axe-core | 4.10.2 | MPL-2.0 | vendored-file | sha256:b511cd9dec01c76f4b2ad1723b66b6db37d4c2eb4ed199076e1829d9ee7b75e3 |

## 3. Container images (pinned by digest)

The packages inside these images (distribution packages with GPL and LGPL parts, among others) are not listed here; they are in `sbom/image-*.cdx.json` of an offline bundle built with `syft`, and the corresponding-source question for them is an open item of the legal packet.

| Image | Tag | Pin | Digest (sha256) |
|---|---|---|---|
| caddy | 2.10 | KEEL_PROXY_IMAGE | c3d7ee5d2b11f9dc54f947f68a734c84e9c9666c92c88a7f30b9cba5da182adb |
| chrislusf/seaweedfs | 4.47 | KEEL_VAULT_IMAGE | ce9e796f1fe6f06968f4c04bdaf8f678dad9c8acdfef3d244133d71bfa6bf882 |
| postgres | 17-alpine | KEEL_POSTGRES_IMAGE | 266d3c257594e195b309ae9d1805e54c788f1d7b375f56bf6c367aac6c917e7b |
| registry.access.redhat.com/ubi9/dotnet-100 | latest | SDK_IMAGE | 6938571c7256856fca0a82b4b8a5ad0534b3b6994a2449b9f82842a0fab64e79 |
| registry.access.redhat.com/ubi9/dotnet-100-aspnet | latest | RUNTIME_IMAGE | 1c10d6cffa50eeb7247e3739bfe30d7cd52cc6cc88eb9ce05438bf3eacd3ca12 |

## 4. Keel's own add-on

First-party code that is delivered but is not third-party software (listed so that its dependency statement is on record).

| Component | Version | Runtime dependencies | Files digest (sha256) |
|---|---|---|---|
| keel-freecad-addon | 0.1.0 | Python standard library and the FreeCAD host application only (no third-party Python package is imported by the add-on) | 5c02c416a5dbe24308ec5086d3d6d1bbde511ea6ac0b71c783e5ff0659161874 |

## 5. Licence expressions in use

| Licence | Delivered | Not delivered |
|---|---|---|
| Apache-2.0 | 6 | 19 |
| Bitstream-Vera | 2 | 0 |
| LGPL-2.0-or-later | 0 | 1 |
| LGPL-2.1 | 3 | 0 |
| LGPL-2.1-only WITH OCCT-exception-1.0 | 1 | 0 |
| LGPL-3.0-only | 0 | 1 |
| MIT | 22 | 141 |
| MIT AND Apache-2.0 WITH LLVM-exception | 1 | 0 |
| MPL-2.0 | 0 | 1 |
| PostgreSQL | 1 | 0 |

Policy checked by `keel-sbom licenses --check`: every component declares a licence; none is under AGPL, SSPL, BUSL, a non-commercial or a plain GPL licence (weak copyleft such as LGPL and MPL-2.0 is allowed only where this document and the legal packet already record it, and every delivered copyleft component needs a pinned source archive in `deploy/offline/third-party-sources.json`).

