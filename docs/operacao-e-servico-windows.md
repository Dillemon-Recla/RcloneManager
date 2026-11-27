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

## Modos de Execução em Segundo Plano (Seletor na Etapa 4 do Wizard)

Na Etapa 4 do **Assistente Inicial (Wizard)** (e na aba **Logs & Serviço**), o usuário escolhe explicitamente entre três modos claros:

### 1. Modo Usuário Silencioso (`UserStartup` / `--background`) — Padrão Recomendado
- Iniciado automaticamente pela interface quando `http://127.0.0.1:5580` não responde e registrado em `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` (`RcloneManagerService`).
- **Vantagem**: não solicita permissão de Administrador (UAC) e roda na sessão do usuário com acesso direto às pastas pessoais e `%AppData%\rclone\rclone.conf`.

### 2. Serviço Nativo do Windows (`WindowsService` / `--windows-service`)
- Pode ser selecionado diretamente na **Etapa 4 do Wizard** ou nos botões **Instalar WinService** / **Remover WinService** na aba **Logs & Serviço** (solicita confirmação UAC uma única vez).
- O instalador libera a porta `5580` da sessão atual, registra/atualiza o serviço com `start= auto` + auto-restart (`sc failure`) e executa `sc start RcloneManagerService`.
- **Compatibilidade com `LocalSystem`**: o `AppConfigStore` grava explicitamente `rcloneConfigPath` e o `WindowsCredentialMasterKeyStore` mantém um espelho DPAPI `LocalMachine` em `%ProgramData%\RcloneManager\vault_*.dpapi`, permitindo que o serviço sob `LocalSystem` decifre `rclone_manager_config.json` sem perder caminhos ou `extraFlags`.

### 3. Somente Manual (`ManualOnly`)
- Mantém o serviço ativo apenas enquanto o aplicativo for iniciado manualmente pelo usuário, sem registrar inicialização com o Windows.

---

## Menus da Interface (`Arquivo` e `Ajuda`)

- **Arquivo**: `Nova Tarefa` (`Ctrl+N`), `Salvar Alterações da Aba Atual` (`Ctrl+S`), atalhos rápidos para abrir pasta de trabalho / `rclone_manager_config.json` / pasta de logs, **Exportar Configuração** (portátil sem credenciais), **Importar Configuração** (via Wizard), **Executar Assistente Inicial (Wizard)**, **Resetar / Deletar Configurações**, minimizar para bandeja e sair.
- **Ajuda**: abrir pasta `docs/`, repositório no GitHub, documentação oficial do Rclone, **Verificar Release Travada**, **Copiar Informações de Diagnóstico** e janela **Sobre** (com versões em execução, licença MIT e lista de componentes).

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
  - Guarda `rcloneExecutablePath`, `rcloneConfigPath`, `workingDirectory`, `pinnedRcloneVersion` (`v1.76.0`), `lockRcloneVersion` (`true`), `firstRunCompleted`, `encryptionEnabled`, `encryptionKeyTarget` (`RcloneManager:MasterKey`), `protectedSecrets`, portas (`5580` e `5572`), `globalBandwidthLimit` e a lista `jobs`.
  - Campos sensíveis (`rcloneConfigPath`, `protectedSecrets` e `extraFlags` das tarefas) são gravados cifrados com **AES-256-GCM** (`enc:v1:...`) usando a chave armazenada no Gerenciador de Credenciais do Windows.
- **`rclone_manager_service.log`**: registra eventos do serviço, escolha da estratégia híbrida, avisos de sanitização de filtro e erros do agendador.
- **`rclone_backup.log`**: log padrão gravado pelo `rclone.exe` durante execuções de tarefas (`--log-file`).

---

## Solução de Problemas Comuns

- **Erro `lookup www.googleapis.com: no such host`**: indica falta temporária de rede/DNS. Habilite `RetryOnFailure` no agendador (padrão: 2 retentativas com espera de 5 minutos) e mantenha `--retries 3 --low-level-retries 5` em `ExtraFlags`.
- **Erro ao instalar Windows Service**: a operação usa `sc.exe` e requer confirmação de Administrador (UAC). Se recusar o UAC, utilize a opção por usuário (`HKCU\...\Run`).
- **Execução bloqueada por versão divergente**: se a trava estiver ativa e um `rclone.exe` de outra versão for colocado manualmente na pasta, use o botão **Baixar / Travar Release** na aba **Logs & Serviço** para provisionar a release travada ou desmarque a trava se desejar usar outro binário.
