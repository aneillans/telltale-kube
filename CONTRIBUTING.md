# Contributing

Telltale Kube aims to stay small, simple and secure. 

These rules follow from that.

## Rules

- Adding a package needs a reason in the PR description. Prefer BCL where possible. CI fails if the shipped desktop build
  grows past 60 MB.
- Keep code AOT and trim safe: no reflection-based serialisation, no `dynamic`, compiled bindings with `x:DataType`
  in every view unless there is a really good reason, and every new view registered in the `App.axaml` data-template map.
- All code needs tests and a review.
- Never log or display secrets. Route user-visible error text through `Redactor.Redact`.
- Mutating actions must respect `ClusterViewModel.CanMutate`. 
- Comment only where the reason is not obvious from the code.
- Update the docs.

## Techie

```sh
dotnet build TelltaleKube.Portable.slnf
dotnet format TelltaleKube.Portable.slnf --verify-no-changes
dotnet test --project tests/TelltaleKube.Core.Tests
dotnet test --project tests/TelltaleKube.App.Tests
```

For UI changes, attach the before and after screenshots the App tests generate as evidence to your PR.
