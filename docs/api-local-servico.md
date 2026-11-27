# API HTTP Local e Modos do Serviço

## Contexto

O projeto `RcloneManager.Service` atua como daemon de controle e agendamento desacoplado da interface gráfica. Ele escuta localmente via Kestrel em **`http://127.0.0.1:5580`** (porta configurável via `serviceApiPort` em `rclone_manager_config.json`), utilizando serialização JSON `camelCase` e enums como strings (`JsonStringEnumConverter`).

---

## Endpoints da API Local (`src/RcloneManager.Service/Program.cs`)

### Status e Monitoramento
| Método | Rota | Descrição |
| :--- | :--- | :--- |
| `GET` | `/api/status` | Retorna `ServiceStatusDto` completo: estado do serviço, versão do Rclone, status da trava (`pinnedRcloneVersion`, `lockRcloneVersion`, `isRcloneVersionValid`), cofre/wizard (`firstRunCompleted`, `encryptionKeyActive`, `encryptionKeyTarget`), daemon `rcd`, banda atual, execução ativa, `/core/stats` ao vivo, lista de tarefas e logs recentes. |
| `GET` | `/api/logs?maxLines=200` | Retorna as últimas linhas (`10` a `2000`) de `serviceLogs` (buffer em memória) e `rcloneLogLines` (leitura não bloqueante com `FileShare.ReadWrite \| FileShare.Delete`). |

### Gerenciamento de Tarefas (`Jobs`)
| Método | Rota | Descrição |
| :--- | :--- | :--- |
| `GET` | `/api/jobs` | Lista todas as tarefas configuradas. |
| `POST` | `/api/jobs` | Cria ou atualiza uma `SyncJob` (exige `sourcePath` e `destinationPath`) e recalcula `nextRunAt`. |
| `DELETE` | `/api/jobs/{id}` | Remove uma tarefa pelo `id`. |
| `POST` | `/api/jobs/{id}/run?dryRun=false` | Enfileira execução manual; aceita corpo opcional `RunJobRequest` (`dryRun`, `overrideStrategy`). |
| `POST` | `/api/jobs/{id}/stop` | Solicita cancelamento da tarefa em execução. |
| `GET` | `/api/jobs/{id}/excluded-retention?scan=false\|true` | Retorna `ExcludedRetentionStatusDto` com a contagem e pastas em carência de exclusão (30 dias por padrão); com `scan=true`, inspeciona o destino ao vivo via `--fast-list` e remove itens vencidos. |
| `POST` | `/api/jobs/{id}/purge-excluded` | Expurga imediatamente do destino todos os arquivos hoje bloqueados pelo filtro da tarefa (`PurgeExcludedResultDto`). |

### Controle Híbrido RC, Banda e Release
| Método | Rota | Descrição |
| :--- | :--- | :--- |
| `POST` | `/api/bwlimit` | Recebe `{ "rate": "10M" }` (ou `"off"`), persiste em `globalBandwidthLimit` e aplica ao vivo no job ativo e no daemon `rcd` via `/core/bwlimit`. |
| `POST` | `/api/rcd/toggle` | Alterna o daemon persistente `rclone rcd` (porta `5572`) e atualiza `autoStartRcdDaemon`. |
| `GET` | `/api/remotes` | Lista remotes configurados usando RC API (se `rcd` ativo) com fallback para `rclone listremotes`. |
| `GET` | `/api/remotes/details` | Lista remotes com tipo, status de token (`hasToken`) e parâmetros com segredos mascarados (`••••••••`) via `/config/dump`. |
| `GET` | `/api/remotes/providers` | Lista os provedores suportados pelo Rclone e metadados de suas opções (`/config/providers` com cache). |
| `POST` | `/api/remotes/save` | Cria ou atualiza um remote (`CreateOrUpdateRemoteRequest`) nos 3 modos (`OAuthBrowserLink`, `ManualTokenJson`, `KeyOrCredentials`). |
| `POST` | `/api/remotes/{name}/reconnect` | Dispara `rclone config reconnect <name>:` para renovar OAuth no navegador. |
| `POST` | `/api/remotes/{name}/test` | Testa o remote via `rclone lsd <name>: --max-depth 1` e consulta `/operations/about`. |
| `DELETE` | `/api/remotes/{name}` | Remove um remote do `rclone.conf` via `/config/delete` (ou CLI). |
| `GET` | `/api/about?remote=gdrive:` | Consulta espaço total, usado, livre e lixeira do remote informado. |
| `POST` | `/api/rclone/stop-rcd` | Para o daemon `rclone rcd` para liberar o arquivo `rclone.exe` antes de uma troca/atualização de release. |
| `POST` | `/api/rclone/release-config` | Recebe `UpdateReleaseConfigRequest`, valida a versão, atualiza trava/caminho no `JobScheduler` e reinicia o `rclone rcd` se habilitado. |

### Filtros e Controle do Serviço
| Método | Rota | Descrição |
| :--- | :--- | :--- |
| `GET` | `/api/filters/analyze?path=...` | Analisa o arquivo de filtros (`exclude_user_folder.txt` por padrão) e retorna `FilterAnalysisResult` + conteúdo atual. |
| `POST` | `/api/filters/save` | Salva conteúdo editado em `FilterFilePayload` e retorna nova análise. |
| `POST` | `/api/filters/optimize` | Cria backup `.bak`, reordena e corrige as regras no disco via `FilterFileHelper.ApplyOptimizationWithBackup`. |
| `POST` | `/api/service/startup?enabled=true\|false` | Cria ou remove a entrada `RcloneManagerService` em `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` e sincroniza `preferredServiceMode`. |
| `POST` | `/api/service/mode?mode=UserStartup\|WindowsService\|ManualOnly` | Define o modo preferencial de execução em 2º plano (`BackgroundServiceMode`) e ajusta a chave de inicialização do usuário. |
| `POST` | `/api/service/complete-first-run` | Marca `firstRunCompleted = true` na configuração persistida. |
| `POST` | `/api/service/reload` | Para o `rcd` se ativo, recarrega `JobScheduler` do disco (`rclone_manager_config.json`) e reinicia o `rcd`. |
| `POST` | `/api/service/reset` | Aceita `ResetConfigRequest` (`deleteCredentialKey`, `deleteLogsAndRuntime`, `deleteRcloneConf`), limpa conforme escolhido e reinicia o config. |
| `POST` | `/api/service/shutdown` | Encerra graciosamente o serviço (`IHostApplicationLifetime.StopApplication()`). |

---

## Argumentos de Linha de Comando (`RcloneManager.Service.exe`)

- `--workdir <caminho>`: define explicitamente o diretório raiz onde residem `rclone_manager_config.json` e logs.
- `--background`: suprime os provedores de log de console para execução silenciosa em 2º plano.
- `--windows-service`: ativa o `WindowsServiceHost` (dispatcher nativo `StartServiceCtrlDispatcherW` / `RegisterServiceCtrlHandlerExW` / `SetServiceStatus` em `advapi32.dll`).
- `--install-service`: cria ou atualiza `RcloneManagerService` (`sc.exe create` / `sc.exe config`), define política de auto-recuperação (`sc.exe failure`) e inicia o serviço imediatamente (`sc.exe start`).
- `--uninstall-service`: executa `sc.exe stop` e `sc.exe delete RcloneManagerService`.

---

## Validacao

Smoke test executado após a migração para `.NET 10`:
```powershell
Start-Process "c:\rclone\src\RcloneManager.Service\bin\Release\net10.0\RcloneManager.Service.exe" -ArgumentList "--background" -WindowStyle Hidden
Invoke-RestMethod -Uri "http://127.0.0.1:5580/api/status"
```
Saída observada:
- `serviceRunning: true`
- `rcloneVersion: "v1.76.0"`
- `pinnedRcloneVersion: "v1.76.0"`
- `lockRcloneVersion: true`
- `isRcloneVersionValid: true`
- `rcDaemonRunning: true` (`rcDaemonPort: 5572`)

---

## Observacoes / Pontos de atencao

- **Superfície de rede**: a API escuta exclusivamente em `127.0.0.1` (loopback local) e não possui autenticação HTTP, não devendo ser exposta em interfaces externas sem adicionar autenticação/TLS.
- **Permissões administrativas**: `--install-service` e `--uninstall-service` invocam `sc.exe` e exigem elevação UAC (`runas`).
