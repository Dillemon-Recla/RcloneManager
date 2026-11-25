# Arquitetura e Motor Híbrido (RC API + CLI)

## Contexto

O `RcloneManager` foi projetado para separar a **interface visual** do **motor de execução e agendamento**, garantindo que tarefas de sincronização e backup continuem rodando em segundo plano mesmo quando a janela principal é fechada. Além disso, o controle sobre o `rclone.exe` adota uma abordagem **híbrida**, escolhendo entre a API HTTP Remote Control (`rcd` / `--rc`) e a linha de comando (`CLI`) conforme a necessidade de cada operação.

---

## Estrutura dos Projetos

### 1. `src/RcloneManager.Core` (`net10.0`)
Biblioteca compartilhada sem dependências externas de NuGet:
- `Models/SyncJob.cs`: definição de tarefas (`Sync`, `Copy`, `Move`, `Check`), flags (`SkipLinks`, `FastList`, `CopyLinks`, `DryRun`, `MaxSize`, `BandwidthLimit`, `ExtraFlags`), estratégia (`ExecutionStrategy`) e agendamento (`JobSchedule`).
- `Models/RcloneStats.cs`: DTOs tipados para desserializar `/core/stats`, `/core/bwlimit` e `/operations/about` do Rclone.
- `Models/ServiceStatusDto.cs`: configuração global (`AppManagerConfig`) e payload completo de status (`ServiceStatusDto`).
- `Engine/RcloneHybridEngine.cs`: orquestrador híbrido (daemon `rclone rcd`, execução CLI monitorada via RC e fallback CLI puro).
- `Engine/RcloneRcClient.cs`: cliente HTTP para os endpoints RC (`/core/pid`, `/core/version`, `/config/listremotes`, `/core/stats`, `/core/bwlimit`, `/operations/about`, `/sync/*`, `/job/*`, `/core/quit`).
- `Engine/RcloneCliRunner.cs`: construção segura de `ProcessStartInfo` com `ArgumentList` (sem janela e sem problemas de escape de aspas).
- `Engine/RcloneReleaseManager.cs`: verificação, download, validação SHA-256 e trava de versão do `rclone.exe`.
- `Engine/FilterFileHelper.cs`: analisador e sanitizador de arquivos `--filter-from` / `--exclude-from`.
- `Scheduling/JobScheduler.cs`: fila de execução manual, cálculo de `NextRunAt`, controle de retentativas e persistência via `AppConfigStore`.
- `Storage/WorkspacePathResolver.cs` e `Storage/AppConfigStore.cs`: descoberta da raiz do projeto/binários e leitura/escrita atômica (`.tmp` + `File.Move`) de `rclone_manager_config.json`.

### 2. `src/RcloneManager.Service` (`net10.0`)
Host ASP.NET Core que executa `RcloneBackgroundWorker` (`BackgroundService`):
- Mantém o `JobScheduler` em tick de `1` segundo e gerencia o ciclo de vida do `rclone rcd`.
- Expõe API HTTP local exclusivamente em `127.0.0.1:5580`.
- Inclui `WindowsServiceHost.cs` (dispatcher SCM nativo via P/Invoke em `advapi32.dll`) para rodar como Serviço do Windows sem pacotes NuGet extras.

### 3. `src/RcloneManager.UI` (`net10.0-windows`)
Aplicação WPF (Dark UI) com `NotifyIcon` (`TrayIconHelper`):
- Provisiona o `rclone.exe` antes de conectar ao serviço (`RcloneSetupWindow`).
- Inicia `RcloneManager.Service.exe --background` automaticamente via `ServiceLauncher` se a porta `5580` não estiver respondendo.
- Faz polling em `GET /api/status` a cada `1500 ms` para atualizar KPIs, velocidade, ETA e arquivos em transferência.

---

## Como o Motor Híbrido Decide (`RcloneHybridEngine.ResolveStrategy`)

Quando a tarefa usa `ExecutionStrategy.HybridAuto`:

1. **Avaliação para `CliWithRcMonitoring`**:
   - Escolhida automaticamente se a tarefa utiliza `--filter-from` / `--exclude-from`, `--max-size`, `--log-file`, `ExtraFlags`, `--skip-links`, `--fast-list` ou `--copy-links`.
   - Inicia `rclone.exe` em processo CLI dedicado com todas as flags completas **mais** `--rc --rc-addr=127.0.0.1:<portaLivre> --rc-no-auth --stats=1s`.
   - Durante a execução, `RcloneRcClient` consulta `/core/stats` a cada 1 segundo e permite alterar `--bwlimit` ao vivo via `/core/bwlimit`.
2. **Avaliação para `RcDaemonApi`**:
   - Escolhida para operações simples (sem flags exclusivas de CLI) quando o daemon persistente `rclone rcd` (`127.0.0.1:5572`) está ativo.
   - Dispara `/sync/sync`, `/sync/copy`, `/sync/move` ou `/operations/check` com `_async = true` e acompanha via `/job/status` e `/core/stats`.
   - Se a chamada via daemon falhar (desde que não tenha sido cancelada pelo usuário), aplica **fallback automático** para `CliWithRcMonitoring`.
3. **Consultas híbridas (`ListRemotesHybridAsync`, `GetRemoteAboutAsync`, `GetRcloneVersionAsync`)**:
   - Tentam primeiro o daemon `rclone rcd` via HTTP (resposta imediata) e recorrem ao CLI (`rclone listremotes`, `rclone about --json`, `rclone version`) caso o daemon esteja desligado ou falhe.

---

## Agendador e Resolução de Diretórios

- **`JobScheduler`**:
  - Usa `SemaphoreSlim(1, 1)` para evitar execuções concorrentes de sincronização que disputem disco e rede.
  - Calcula `NextRunAt` para `IntervalMinutes`, `DailyAtTime`, `WeeklyDays` e dispara `OnServiceStart` uma única vez na inicialização do serviço.
  - Em falhas de execuções agendadas (`RetryOnFailure = true`), agenda nova tentativa após `RetryDelayMinutes` até atingir `MaxRetries`.
- **`WorkspacePathResolver.ResolveRootDirectory`**:
  - Prioriza `--workdir` e `RCLONE_MANAGER_HOME`; em seguida sobe até 6 níveis a partir de `AppContext.BaseDirectory` procurando `rclone_manager_config.json` ou `RcloneManager.sln`, e como alternativa procura `rclone.exe` diretamente ou em subpastas `rclone-*`.

---

## Validacao

Compilação completa em `.NET 10` (`Release`):
```powershell
dotnet build c:\rclone\RcloneManager.sln -c Release
```
Resultado observado: `Compilação com êxito. 0 Aviso(s), 0 Erro(s)` gerando `RcloneManager.Core.dll`, `RcloneManager.Service.dll` (`net10.0`) e `RcloneManager.UI.dll` (`net10.0-windows`).

Teste integrado local (executado em pasta temporária `%TEMP%` via API do serviço):
- Criação de tarefa temporária `Copy` (`HybridAuto`), disparo por `POST /api/jobs/{id}/run`, sanitização automática do filtro e conclusão com `lastStatus: "Success"`.

---

## Observacoes / Pontos de atencao

- Apenas uma tarefa de transferência executa por vez (`_executionLock` no `JobScheduler` e `_activeExecution` no `RcloneHybridEngine`); novas requisições manuais entram na fila `_manualQueue` e rodam na sequência.
- O encerramento de processos Rclone em execução tenta primeiro `/core/quit` via RC e só usa `Process.Kill(entireProcessTree: true)` se não finalizar em 2 segundos.
