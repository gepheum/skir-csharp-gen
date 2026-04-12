#!/bin/bash

set -e

npm i
npm run lint:fix
npm run format
npm run build
npm run test
# Fail if there are any C# warnings (e.g. unused imports) in non-generated code.
# Warnings for generated files under skirout/ are already suppressed via <NoWarn> in
# the .csproj, so -warnaserror only affects hand-written source files.
cd e2e-test && dotnet build CsharpE2eTest.csproj -warnaserror && dotnet test SkirClientTest/SkirClientTest.csproj && cd ..
