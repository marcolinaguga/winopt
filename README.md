# Winopt

Aplicativo desktop para limpeza e manutenção do Windows. Reúne em uma única janela as ferramentas nativas do sistema (SFC, DISM, CHKDSK, Limpeza de Disco, desfragmentador) e limpezas de arquivos temporários e caches, com log em tempo real.

Feito em C# com WinForms para .NET Framework 4.8, sem pacotes NuGet nem bibliotecas de terceiros.

## Ferramentas

**Limpeza**

- Arquivos temporários (`%TEMP%` e `C:\Windows\Temp`)
- Lixeira
- Cache dos navegadores (Chrome, Edge, Brave e Firefox): apenas o cache; senhas, cookies, histórico e favoritos não são tocados
- Despejos de memória e relatórios de erro
- Cache DNS
- Cache do Windows Update
- Limpeza de Disco (`cleanmgr`), restrita a categorias seguras; a pasta Downloads nunca é incluída
- Componentes antigos do Windows (`DISM /StartComponentCleanup`)

**Reparo**

- Restaurar imagem do Windows (`DISM /RestoreHealth`)
- Verificar integridade dos arquivos do sistema (`sfc /scannow`)
- Verificar disco (`chkdsk /scan`)

**Otimização**

- Otimizar unidades (`defrag /C /O`): TRIM em SSD, desfragmentação em HD

## Recursos

- **Analisar**: estima quanto espaço cada limpeza liberaria, sem apagar nada.
- **Cancelar**: interrompe a ferramenta em andamento.
- **Ponto de restauração**: criado antes de reparos, se a opção estiver marcada.
- Barra de progresso com a porcentagem real do SFC, DISM e CHKDSK.
- Resumo do espaço livre antes e depois.
- Log de cada execução salvo em `%LOCALAPPDATA%\Winopt\Logs`.

## O que o programa não faz

Não há limpeza de Registro, "liberador de memória" nem desativação de serviços. Arquivos em uso por outros programas são ignorados, e atalhos de pasta (junções) dentro das pastas temporárias nunca são seguidos.

## Requisitos

- Windows 10 ou 11
- .NET Framework 4.8 (já vem com o Windows)
- Permissão de administrador: o programa pede elevação (UAC) ao abrir

## Como compilar

**Sem Visual Studio:** execute `compilar.bat`. Ele usa o compilador C# que acompanha o .NET Framework e gera `bin\Winopt.exe`.

**Com Visual Studio:** abra `Winopt.csproj`, selecione a configuração `Release` e compile (`Ctrl+Shift+B`). É necessário o workload "Desenvolvimento para desktop com .NET". O executável sai em `bin\Release`.

## Estrutura do código

| Arquivo | Conteúdo |
| --- | --- |
| `Form1.cs` | Janela, fila de execução, progresso e log |
| `Etapas.cs` | Catálogo das ferramentas e a implementação de cada uma |
| `Processos.cs` | Execução silenciosa de utilitários e leitura da saída |
| `Arquivos.cs` | Exclusão e medição segura de arquivos |
| `Contexto.cs` | Canal entre as ferramentas e a interface (log, progresso, cancelamento) |
| `app.manifest` | Elevação para administrador e compatibilidade |

## Aviso

Use por sua conta e risco. As ferramentas de reparo alteram componentes do sistema; mantenha a opção de ponto de restauração marcada ao usá-las.
