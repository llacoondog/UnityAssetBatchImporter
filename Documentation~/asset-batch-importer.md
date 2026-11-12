# Asset Batch Importer

## Workflow

`My Assets -> Label -> Download Missing -> Wait for cache -> Sequential Import`

The Asset Store integration is isolated in `UnityAssetStoreBridge.cs` because it relies on Unity internal Package Manager services.

## Diagnostics

Use **진단 복사** in the toolbar. The report contains the tool and Unity versions and the Asset Store bridge status.

## Versioning recommendation

When hosted on GitHub, create release tags such as `v0.3.0`, `v0.3.1`, etc. Pin Unity projects to a known-good tag rather than always tracking the repository head.
