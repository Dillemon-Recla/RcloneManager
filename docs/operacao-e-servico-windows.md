# Operação, Serviço Windows e Solução de Problemas

## Contexto

Este guia cobre o build, os modos de execução da interface e do serviço em segundo plano no Windows, a estrutura do arquivo de configuração e os arquivos de log gerados em runtime.

---

## Compilação e Inicialização

### Build via CLI (.NET 10 SDK)
```powershell
cd c:\rclone
dotnet build RcloneManager.sln -c Release
```
Saídas geradas:
- Serviço: `src\RcloneManager.Service\bin\Release\net10.0\RcloneManager.Service.exe`
- Interface: `src\RcloneManager.UI\bin\Release\net10.0-windows\RcloneManager.UI.exe`

### Execução Rápida
Execute `Iniciar-RcloneManager.bat` na raiz do repositório. O script compila automaticamente caso os executáveis em `Release` ainda não existam e inicia `RcloneManager.UI.exe`.

---

## Modos de Execução em Segundo Plano

### 1. Modo Usuário Silencioso (`--background`) — Padrão
- Iniciado automaticamente pela interface quando `http://127.0.0.1:5580` não responde.
- Pode ser configurado para iniciar no logon do usuário marcando **"Iniciar Serviço automaticamente com o Windows (HKCU\...\Run)"** na aba **Logs & Serviço** (cria o valor `RcloneManagerService` em `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`).
- Vantagem: roda na sessão do usuário, enxergando automaticamente o perfil e `%AppData%\rclone\rclone.conf`.

### 2. Serviço Nativo do Windows (`--windows-service`)
- Registrado via botões **Instalar WinService** / **Remover WinService** na aba **Logs & Serviço** (ou executando em prompt Administrador `RcloneManager.Service.exe --install-service --workdir "c:\rclone"`).
- **Atenção**: Serviços do Windows rodam por padrão como `LocalSystem` (que não tem o mesmo `%AppData%` do usuário). Por isso o `AppConfigStore` grava explicitamente `rcloneConfigPath` em `rclone_manager_config.json` e o `RcloneCliRunner` / `RcloneHybridEngine` repassam `--config "<caminho>"` para o `rclone.exe`.

---

## Bandeja do Sistema (`TrayIconHelper`)

Ao clicar no botão fechar (`X`) da janela principal, você escolhe entre:
- **Sim**: minimizar para a Bandeja do Sistema (mantém a UI oculta e o serviço ativo).
- **Não**: fechar apenas a interface (o serviço e o agendador continuam rodando em segundo plano).
- **Cancelar**: permanecer na janela.

O menu de contexto do ícone na bandeja permite reabrir o painel, executar o backup principal, parar a tarefa em curso ou encerrar **Interface + Serviço**.

---

## Arquivos Locais de Configuração e Logs

- **`rclone_manager_config.json`**:
  - Gerado na primeira execução na raiz do workspace (importa automaticamente `script.bat` e `exclude_user_folder.txt` se existirem).
  - Guarda `rcloneExecutablePath`, `rcloneConfigPath`, `workingDirectory`, `pinnedRcloneVersion` (`v1.76.0`), `lockRcloneVersion` (`true`), portas (`5580` e `5572`), `globalBandwidthLimit` e a lista `jobs`.
- **`rclone_manager_service.log`**: registra eventos do serviço, escolha da estratégia híbrida, avisos de sanitização de filtro e erros do agendador.
- **`rclone_backup.log`**: log padrão gravado pelo `rclone.exe` durante execuções de tarefas (`--log-file`).

---

## Solução de Problemas Comuns

- **Erro `lookup www.googleapis.com: no such host`**: indica falta temporária de rede/DNS. Habilite `RetryOnFailure` no agendador (padrão: 2 retentativas com espera de 5 minutos) e mantenha `--retries 3 --low-level-retries 5` em `ExtraFlags`.
- **Erro ao instalar Windows Service**: a operação usa `sc.exe` e requer confirmação de Administrador (UAC). Se recusar o UAC, utilize a opção por usuário (`HKCU\...\Run`).
- **Execução bloqueada por versão divergente**: se a trava estiver ativa e um `rclone.exe` de outra versão for colocado manualmente na pasta, use o botão **Baixar / Travar Release** na aba **Logs & Serviço** para provisionar a release travada ou desmarque a trava se desejar usar outro binário.
