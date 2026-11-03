# Development

Cruncharr uses .NET 8, nullable C#, xUnit, vanilla browser JavaScript, and Node's
built-in test runner. Install a .NET SDK that targets .NET 8, the .NET 8 and
ASP.NET Core 8 runtimes, Node.js 22 or newer, Git, and FFmpeg.

From the repository root:

```sh
node scripts/quality.cjs --base <base-commit>
```

Use the commit before your changes as the base. The command checks whitespace,
JavaScript syntax and tests, changed C# formatting, a Release build with compiler
analyzers, and the full .NET test suite. It does not modify source files.
FFmpeg must be on PATH, or `CRUNCHARR_TEST_FFMPEG` must name its executable.

For focused checks:

```sh
dotnet build cruncharr.sln --configuration Release
dotnet test src/Cruncharr.Core.Tests/Cruncharr.Core.Tests.csproj
node --test scripts/queue-order.test.js
```

Verify UI changes in a browser and affected integrations against a test instance.
Never use live account credentials in tests or commit runtime configuration.
