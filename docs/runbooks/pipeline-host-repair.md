# Review and retain the Pipeline host repair

Review [issue #621](https://github.com/Ambiguous-Interactive/DxMessaging/issues/621) before
installing this candidate. Its native controls and affected Unity scopes passed locally;
durable installation and wallstop review remain open. This repair affects the local Unity
verification transport. It is outside the DxMessaging runtime package.

## Exact inputs

Use [the retained patch](../../scripts/mcp/pipeline-0.8.0-exp.1-lifecycle.patch) only with
`com.unity.pipeline` version `0.8.0-exp.1`, repository revision
`dde7080264d32a1091171d4b358351279fa10ddb`. The tested host uses Unity `6000.4.6f1`,
ARM64 Mono. Other package versions and runtimes require separate verification.

The patch changes only `Runtime/Common/BasePipelineServer.cs`. Admit that file by its
SHA256, preserving its original LF bytes:

| State                     | SHA256                                                             |
| ------------------------- | ------------------------------------------------------------------ |
| Original vendor source    | `18dbf11eedefbd355cfdb2fb7dcd1342ecd6a404011ee4bf3f0d84ac0c3d3d1f` |
| Complete tested candidate | `3cf8b76e43158994847e993d1fa0cd536b72e53b49bc533105ea4b50d8060a2d` |

Patch SHA256: `75777c97be7b6871556809b9895ef50e77849a791ef908a6677082b8dc2df4b2`.

Stop if the source matches neither hash. Preserve unexpected changes for review. Keep
the vendor's license and notices intact. The patch preserves loopback, authentication,
execution admission and scheduling. Unknown peer identity still fails closed; genuine
handler faults remain full Errors. Ended transports retain full diagnostics.

## Prepare the reviewed installation

1. Require an inactive Test Framework, stopped Play Mode, no compilation or update,
   the main stage, and clean original scenes. Preserve the project manifest, lock file,
   package inventory, full source hashes and original vendor source outside `Assets`.
1. Confirm the resolved package identity through Unity's Package Manager. A virtual
   `Packages/com.unity.pipeline` path or a successful file read alone does not prove
   embedding; record the actual package source and resolved path.
1. Obtain wallstop's review of the patch, native evidence, installation and rollback.
1. Use the Package Manager's **Manage > Customize** operation to embed the admitted
   version. Unity documents that an embedded package overrides the cached dependency
   and should be tracked with the host project. Preserve existing embedded content;
   stop if the destination is already owned by someone else.
1. Compare the embedded package against the recorded inventory. Apart from this one
   admitted file, all source and metadata must remain exact. Record any Package Manager
   changes to the manifest and lock file for review.

Follow [Unity's embedding instructions](https://docs.unity.com/en-us/engine/6000.3/manual/packages-list/dependencies-lp/upm-embed).
The tested cache edit is temporary; editing the registry cache does not establish a
durable installation.

## Keep shipping and transport verification explicit

Pipeline `0.8.0-exp.1` declares Unity `6000.0` as its minimum editor version.
Do not add it to the general shipping harness, which supports Unity `2021.3`.
The runtime package remains dependency-free. Keep every transport control in
`Tests/Editor/PipelineTransportResearch`; ordinary shipping selection does not run it.

The assembly selector classifies these controls separately. The composite action
reports their names through `excluded-transport-controls` and a notice when they
are excluded from EditMode. A passing shipping result does not qualify transport.
PlayMode and Standalone cannot select these Editor controls.

After the reviewed installation, select transport explicitly on the admitted
Unity `6000.4.6f1` ARM64 Mono host. Read its actual editor version and the resolved
package path through Package Manager. Require the exact package revision and
the source hash in this runbook before compilation or execution. A declared
package version alone does not prove the loaded repair.

From the DxMessaging checkout on that host, pass the observed version and
resolved Pipeline directory through environment variables:

```powershell
$env:DXM_TRANSPORT_PACKAGE_PATH = $packagePath
$env:DXM_TRANSPORT_UNITY_VERSION = '6000.4.6f1'
node -e "const m=require('./scripts/unity/lib/asmdef-discovery'); console.log(JSON.stringify(m.resolveTestAssemblySelection(process.cwd(), {target:'editmode', includeTransportControls:true, transportPackagePath:process.env.DXM_TRANSPORT_PACKAGE_PATH, unityVersion:process.env.DXM_TRANSPORT_UNITY_VERSION})))"
if ($LASTEXITCODE -ne 0) { throw 'Transport scope admission failed.' }
```

The composite action uses `include-transport-controls: true`, `transport-package-path`
and `unity-version` for the same admission. Missing dependencies, a different
package revision, an unqualified editor or an absent fixture fail discovery.
This only verifies selection. Run the full named transport fixture and all retained
endpoint/response controls through the maintained MCP runner. Retain their raw
identities, context release and Error classification separately from the affected
shipping Editor/PlayMode results. Require both scopes and the installation/cleanup
evidence before closing #621. No control is removed or weakened by the opt-in.

## Apply and verify

Run these PowerShell commands on the host against the admitted embedded package.
Set `$packagePath` to its resolved filesystem path and `$patchPath` to the patch's
absolute path. Run the source hash check immediately before applying:

```powershell
$sourcePath = Join-Path $packagePath 'Runtime/Common/BasePipelineServer.cs'
$originalHash = '18dbf11eedefbd355cfdb2fb7dcd1342ecd6a404011ee4bf3f0d84ac0c3d3d1f'
$candidateHash = '3cf8b76e43158994847e993d1fa0cd536b72e53b49bc533105ea4b50d8060a2d'
if ((Get-FileHash -LiteralPath $patchPath -Algorithm SHA256).Hash.ToLowerInvariant() -ne '75777c97be7b6871556809b9895ef50e77849a791ef908a6677082b8dc2df4b2') {
    throw 'Pipeline patch hash mismatch.'
}
$actualHash = (Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actualHash -eq $originalHash) {
    git -C $packagePath apply --check $patchPath
    if ($LASTEXITCODE -ne 0) { throw 'The patch does not match the admitted source.' }
    git -C $packagePath apply $patchPath
    if ($LASTEXITCODE -ne 0) { throw 'Pipeline patch application failed.' }
} elseif ($actualHash -ne $candidateHash) {
    throw 'Unexpected Pipeline source; preserve it for review.'
}
if ((Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash.ToLowerInvariant() -ne $candidateHash) {
    throw 'Pipeline candidate hash mismatch.'
}
```

Refresh through the connected backend's menu after the safe preflight. Confirm the
loaded package now resolves as embedded, the compiled source hash matches, and
authorized requests still work. Repeat the retained endpoint and response controls
and the affected shipping and optional scopes. Require the same case identities,
zero failures or inconclusive results, matching run GUID/path, both result and cleanup
sidecars done, no unexpected transport Errors and original clean scenes restored.
Keep existing interactive optimization skips explicit; they do not verify release
optimization. Qualify samples in a clean fixture without overwriting user imports.

Record the host-project revision and package inventory with the evidence. No accepted
durable installation has been executed yet. A successful patch application alone
does not close #621 or establish its full verification criteria.

## Roll back owned changes

After the same safe preflight, require the current source to match the candidate hash
and all other package files to match the owned inventory. Preserve later edits rather
than overwriting them. Check and reverse the patch:

```powershell
if ((Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash.ToLowerInvariant() -ne $candidateHash) {
    throw 'Pipeline source changed; preserve it for review.'
}
if ((Get-FileHash -LiteralPath $patchPath -Algorithm SHA256).Hash.ToLowerInvariant() -ne '75777c97be7b6871556809b9895ef50e77849a791ef908a6677082b8dc2df4b2') {
    throw 'Pipeline patch hash mismatch.'
}
git -C $packagePath apply --reverse --check $patchPath
if ($LASTEXITCODE -ne 0) { throw 'Pipeline rollback preflight failed.' }
git -C $packagePath apply --reverse $patchPath
if ($LASTEXITCODE -ne 0) { throw 'Pipeline rollback failed.' }
if ((Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash.ToLowerInvariant() -ne $originalHash) {
    throw 'Original Pipeline hash was not restored.'
}
```

Refresh and verify the original source, resolved package and scene state. Remove an
embedded package only if the whole directory is still owned and byte exact; Unity
then resolves the registry dependency again. Restore an owned cache prototype only
after checking its current hash against the recorded candidate. Do not delete user
changes or replace a whole package to repair one file.
