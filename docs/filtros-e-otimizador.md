# Regras de Filtro e Otimizador (`FilterFileHelper`)

## Contexto e Problemas Diagnosticados

O backup legado em `script.bat` utilizava:
```bat
rclone sync "C:\Users\dille" gdrive:BackupPC/ --exclude-from "exclude_user_folder.txt" --skip-links --fast-list --log-file="rclone_backup.log"
```
Ao testar `exclude_user_folder.txt` diretamente com o `rclone.exe`, foram identificados três problemas reais que faziam pastas como `Downloads` e `.gemini` tentarem subir para o Google Drive (gerando erros de acesso e travamento em `rclone_backup.log`):

1. **Flag incompatível (`--exclude-from` vs `--filter-from`)**: o arquivo continha regras de inclusão `+ **.jpg`, que pertencem à sintaxe de `--filter-from`.
2. **Sintaxe malformada para `--filter-from`**: linhas como `/Downloads/**`, `/AppData/**` e `**/.*/**` estavam sem o prefixo `- `, causando erro fatal `malformed rule "/Downloads/**"` ao usar `--filter-from`. Além disso, `- * [-- max-size 50M]` não é uma opção inline válida de filtro no Rclone (o limite de tamanho precisa ir na flag CLI `--max-size 50M`).
3. **Ordem de precedência (*First-Match-Wins*)**: no Rclone, a primeira regra correspondente vence. Como `+ **.json` e `+ **.md` apareciam antes das exclusões de `/AppData/**` e `**/.*/**`, arquivos internos de pastas ocultas eram incluídos antes de chegar na regra de exclusão.

---

## Alterações Aplicadas (`FilterFileHelper.cs`)

Implementado em `src/RcloneManager.Core/Engine/FilterFileHelper.cs`:

- **`Analyze(string filePath)`**:
  - Detecta presença de regras `+ `, ausência de prefixo `- `, uso de `[-- max-size ...]` (extraindo automaticamente o valor, ex.: `50M`) e exclusões posicionadas após includes.
  - Retorna `FilterAnalysisResult` com `RecommendedMode`, `ExtractedMaxSize`, lista de `Warnings` e o conteúdo otimizado (`OptimizedContent`).
- **`ResolveForExecution(...)`**:
  - Em tempo de execução, se o arquivo original ainda possuir alertas incompatíveis com `--filter-from`, o motor **preserva o arquivo original intacto** e gera uma cópia sanitizada em `.rclone-manager-runtime\<nome>.sanitized.filter` (registrando o ajuste no log do serviço).
  - Quando a tarefa está com `MaxSize` vazio e o arquivo possuía `[-- max-size 50M]`, `RcloneCliRunner` aplica `--max-size 50M` na linha de comando.
- **`ApplyOptimizationWithBackup(string filePath)`**:
  - Disponível na aba **Filtros & Otimizador** da UI (e via `POST /api/filters/optimize`): cria um backup `.bak` (ou `.yyyyMMdd_HHmmss.bak` se `.bak` já existir) e grava as exclusões prioritárias antes das regras `+`, substituindo `- * [-- max-size ...]` por `- *`.

---

## Validacao

- Teste com `rclone.exe ls ... --filter-from ".rclone-manager-runtime\exclude_user_folder.sanitized.filter" --dump filters`: o Rclone compilou todas as regras de exclusão e inclusão sem erro de `malformed rule`.
- Chamada a `GET /api/filters/analyze` no arquivo original retornou os 3 alertas esperados (`hasIncludeRules: true`, `hasInvalidInlineMaxSize: true`, `extractedMaxSize: "50M"`, `hasExclusionsAfterIncludes: true`).

---

## Observacoes / Pontos de atencao

- **`--max-size 50M` é global no Rclone**: essa flag limita **todos** os arquivos da tarefa (inclusive vídeos `.mp4`/`.mkv` maiores que 50 MB). Caso queira enviar vídeos grandes mantendo limite de 50 MB apenas para outros tipos, separe em duas tarefas distintas.
- **Padrões de pasta no Rclone**:
  - Padrões iniciados com `/` (ex.: `- /node_modules/**`) filtram **apenas na raiz** da origem; sem barra inicial (`- node_modules/**`, `- bin/**`, `- obj/**`) filtram em qualquer subpasta.
  - Padrão `- **/.*/**` exige uma subpasta antes do ponto; para bloquear pastas ocultas também na raiz (como `.gemini/`), use `- .*/**`.
- **Operação `Sync` vs `Copy`**: `Sync` espelha a origem (pode apagar no destino arquivos removidos localmente); para apenas copiar sem deletar no destino, use `Copy` e valide antes com o botão **Simular (Dry-Run)**.
