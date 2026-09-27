# LaTeX extraction deployment

The LaTeX worker runs separately from the table service on the same Windows
host, using port 18081 and its own runtime, certificates, and SSH tunnel.
These instructions do not imply that a live deployment has occurred.

## Certificates and routing

Run `services/RapidLaTeXOCR/scripts/deployment/Initialize-LatexCommunication.ps1` on a trusted
administration workstation. Its ignored output directory is
`.secrets/deployment/latex-communication`. Keep `authority/` offline. Securely
transfer `worker/` to `C:\ProgramData\SnowShot\pki-latex` on Windows and `api/`
to the API host. Do not mix these files with table worker PKI.

The default certificate covers `snowshot-latex-rec` and `192.168.0.203`;
override the generator parameters for another deployment.

For reverse SSH, bind the worker to loopback. From an elevated Windows prompt:

```powershell
.\services\RapidLaTeXOCR\scripts\deployment\Install-LatexReverseTunnel.ps1 -PublicHostKeyFile C:\deploy\api-host-key.pub
```

Obtain the trusted host public key separately. The installer prints the generated
client public key; authorize it on the API host with:

```text
restrict,port-forwarding,permitlisten="127.0.0.1:18081" ssh-ed25519 ... snowshot-latex-tunnel
```

`SnowShotLatexTunnel` starts at boot and retries periodically, using
`C:\ProgramData\SnowShot\ssh-latex`. It forwards API-host `127.0.0.1:18081`
to Windows `127.0.0.1:18081`. Map `snowshot-latex-rec` to loopback on the API
host; use host networking for a containerized API with this topology. Do not
expose port 18081 publicly. Alternatively, bind to a Tailscale address and map
the certificate hostname to that address in the API container.

## Install and verify

With Python 3.12 installed, from an elevated PowerShell prompt:

```powershell
.\services\RapidLaTeXOCR\scripts\Install.ps1 `
  -ListenHost 127.0.0.1 `
  -ServiceEnvironment production `
  -TlsCertificate C:\ProgramData\SnowShot\pki-latex\worker-server.pem `
  -TlsPrivateKey C:\ProgramData\SnowShot\pki-latex\worker-server-key.pem `
  -TlsClientCa C:\ProgramData\SnowShot\pki-latex\api-client-ca.pem
```

Defaults: service `RapidLaTeXOCRService`, LocalService identity, install directory
`C:\ProgramData\SnowShot\RapidLaTeXOCR`. Installation fails on dependency/model
hash mismatch or unsuccessful DirectML preflight. Run native recognition tests
on the target GPU before accepting traffic.

From a machine with the private route and API certificate bundle, run
`services/RapidLaTeXOCR/scripts/deployment/Test-LatexWorkerMtls.ps1`. It checks connectivity, server
identity, client certificate, and readiness.

## Activate the API

1. Back up the database and current configuration through the normal release process.
2. Run the existing database migrator. `AddLatexExtraction` extends the usage-kind
   constraint to accept kind 3 without changing existing accounting records.
3. Mount the LaTeX API PKI bundle read-only and load its generated environment file.
   For Tailscale, use `deployment/compose.latex-communication.yaml` with
   `SNOWSHOT_LATEX_API_PKI_DIRECTORY` and `LATEX_WORKER_TAILSCALE_IP`. For reverse
   SSH, use the same mTLS settings with the host-network loopback route.
4. Deploy the new binary and policy revision **12** to every replica. Update
   mounted overrides consistently. `latex-extraction` costs 15,000,000 nano-yuan
   input and zero output; existing prices and budgets stay unchanged. Older
   revisions fail existing activation rules once revision 12 becomes active.
5. Verify API readiness includes `latex_worker`. Submit a known cropped WebP
   through `/api/v1/latex/extract`; check the formula and exactly 15,000,000
   nano-yuan public settlement. Table extraction must remain 30,000,000. Invalid,
   empty, and busy outcomes must incur no public charge.

`deployment/policy/policy-revision-12.json` is this release's budget-restoration
overlay. Base settings supply the full resource/pricing configuration. The
previous revision artifact remains as history.

## Monitoring and rollback

Monitor `latex_worker`, extraction latency, admission rejections,
`snowshot.worker.busy` tagged with `latex-extraction`, native watchdog events,
exit code 70, service restarts, and the tunnel task. Logs include IDs, duration,
provider names, and failure category, but no images or formulas.

Before API activation, rollback can stop/uninstall the separate worker and
restore the prior API configuration. After policy 12 activates, use the existing
policy recovery process with a higher revision and compatible binaries; simply
redeploying revision 11 is insufficient. Leave the additive schema constraint
in place. Its down migration is rejected while kind-3 usage rows exist; do not
delete accounting records to force rollback.
