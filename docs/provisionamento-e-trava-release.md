# Provisionamento Automático e Trava de Release do Rclone

## Contexto

Para que o repositório Git permaneça leve (sem versionar `rclone.exe` de ~85 MB) e o comportamento das flags/API RC seja previsível entre máquinas, o `RcloneManager` implementa provisionamento automático com **trava de versão** (`PinnedRcloneVersion` + `LockRcloneVersion`).

Por padrão, a solução trava a release oficial **`v1.76.0`**.

---

## Fluxo de Verificação e Download (`RcloneReleaseManager`)

Implementado em `src/RcloneManager.Core/Engine/RcloneReleaseManager.cs`:

1. **Detecção de Arquitetura e Normalização**:
   - Mapeia `RuntimeInformation.OSArchitecture` para os pacotes Windows oficiais: `X64` → `amd64`, `Arm64` → `arm64`, `X86` → `386`.
   - Normaliza e valida versões com o padrão `^v\d+\.\d+(\.\d+)*(-[a-zA-Z0-9.]+)?$` (ex.: `1.76.0` vira `v1.76.0`).
2. **Checagem (`CheckInstallationAsync`)**:
   - Verifica se `rclone.exe` existe em `rclone-<versão>-windows-<arch>\rclone.exe` (ou no caminho configurado).
   - Executa `rclone.exe version` sem janela para ler a versão instalada real.
   - Marca `NeedsDownload = true` quando o binário não existe **ou** quando `LockRcloneVersion == true` e a versão instalada diverge da travada.
3. **Download com Fallback (`EnsurePinnedReleaseDownloadedAsync`)**:
   - Baixa em streaming para `.rclone-manager-runtime\downloads\<zip>.part` tentando:
     1. `https://downloads.rclone.org/<versão>/rclone-<versão>-windows-<arch>.zip`
     2. Fallback: `https://github.com/rclone/rclone/releases/download/<versão>/rclone-<versão>-windows-<arch>.zip`
4. **Verificação de Integridade SHA-256**:
   - Para `v1.76.0`, compara contra os hashes SHA-256 oficiais embutidos em `BuiltInSha256` (`amd64`: `8b4fd2737ade4e70c057ef4a49deb7cffe94b0fb9d7587c189d149823f21a1dc`).
   - Para outras versões escolhidas pelo usuário, baixa o arquivo oficial `SHA256SUMS` (de `downloads.rclone.org` ou GitHub Releases) e valida o hash do `.zip`. Em caso de divergência, o arquivo temporário é descartado e uma exceção é lançada.
5. **Extração Segura e Validação Pós-Instalação**:
   - Extrai em pasta temporária de staging e copia para `rclone-<versão>-windows-<arch>\` preservando arquivos já existentes do usuário (como scripts, logs e filtros).
   - Executa `rclone.exe version` no executável recém-extraído para confirmar que a versão responde exatamente igual à travada.

---

## Integração na UI e Bloqueio no Serviço

- **Startup da UI (`MainWindow_Loaded` + `RcloneSetupWindow`)**:
  - Antes de subir o `RcloneManager.Service`, a interface executa `CheckInstallationAsync`. Se `NeedsDownload` for verdadeiro, abre a janela modal `RcloneSetupWindow` com barra de progresso em tempo real e atualiza `rclone_manager_config.json` ao concluir.
- **Troca / Reaplicação pela UI**:
  - Na aba **Logs & Serviço**, o card **Release do Rclone (Auto-Download & Trava de Versão)** permite alterar a versão desejada ou ativar/desativar a trava. Antes de baixar/substituir, a UI chama `POST /api/rclone/stop-rcd` para liberar o handle do `rclone.exe` e, ao terminar, envia `POST /api/rclone/release-config` para o serviço recarregar o binário e reiniciar o daemon `rcd`.
- **Bloqueio no Motor (`RcloneHybridEngine.ValidatePinnedVersionAsync`)**:
  - Quando `LockRcloneVersion` está ativo, tanto `StartRcDaemonAsync` quanto `ExecuteJobAsync` recusam rodar se o binário apontado não corresponder exatamente à `PinnedRcloneVersion`.

---

## Validacao

Teste real de provisionamento executado em diretório temporário vazio (`%TEMP%`) após compilação em `.NET`:
1. `CheckInstallationAsync` em pasta vazia retornou `Exists=False, NeedsDownload=True`.
2. `EnsurePinnedReleaseDownloadedAsync` baixou `rclone-v1.76.0-windows-amd64.zip`, validou SHA-256, extraiu e confirmou a versão do executável.
3. Segunda chamada a `CheckInstallationAsync` retornou `Exists=True, Installed=v1.76.0, IsMatch=True, NeedsDownload=False`.

---

## Observacoes / Pontos de atencao

- O auto-download exige conectividade HTTPS com `downloads.rclone.org` ou `github.com` apenas quando o binário local está ausente ou divergente da release travada.
- Se uma versão customizada (beta/fork) não possuir `SHA256SUMS` publicado nas URLs oficiais, a verificação SHA-256 recusará a instalação por segurança.
