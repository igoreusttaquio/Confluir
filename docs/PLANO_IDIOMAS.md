# Confluir — Plano de suporte a idiomas

Idiomas: **Português (Brasil)**, **English** e **Español**.

> **Status: implementado** (antes da fase 3). Diferença em relação ao plano: o `{l:Tr}` faz binding em `LocalizationService.Version` com conversor, em vez do indexador `Item[]`, porque a renotificação do indexador não atualizava as Views. O seletor de idioma está em Aparência.

## Situação atual

- Todas as strings da interface estão em `src/Confluir.App/Resources/Strings.resx` (pt-BR), com classe gerada no build.
- O XAML usa `{x:Static res:Strings.Chave}`, que é avaliado **uma vez** ao criar a View.
- Alguns ViewModels guardam textos prontos (título de pasta, cabeçalhos de data, subtítulo da lista).
- A cultura de formatação (datas, números) está fixa em `pt-BR` em `Program.cs`, `MainWindowViewModel` e `MessageDetailViewModel`.

## Decisões

| Tema | Decisão |
|---|---|
| Arquivos | `Strings.resx` continua sendo o neutro (pt-BR). Novos: `Strings.en.resx` e `Strings.es.resx`; o .NET gera os assemblies satélite. |
| Idioma padrão | "Seguir o sistema": usa o idioma do sistema se for pt/en/es (qualquer variante, ex.: es-MX → es); caso contrário, English. |
| Troca | **Instantânea, sem reiniciar** (como o tema). |
| Formatação | Datas, horas e números seguem o idioma escolhido (pt-BR, en-US, es-ES), salvo o sistema já usar uma variante do mesmo idioma (ex.: en-GB). |
| Logs | Continuam em português; não são exibidos ao usuário. |
| Plurais | Chaves com sufixo `_One` / `_Other` (ex.: `MessageList_Count_One` = "1 mensagem"). |

## Implementação

1. **`LocalizationService`** (App): idioma atual, `CultureInfo` de interface e de formatação, evento `LanguageChanged`. Aplica `CultureInfo.CurrentUICulture`/`CurrentCulture` e a cultura padrão das threads.
2. **Extensão de marcação `{l:Tr Chave}`**: binding para um indexador do `LocalizationService` que notifica `Item[]` ao trocar de idioma, atualizando todas as Views na hora. Substitui todos os `{x:Static res:Strings.…}` (troca mecânica). Compilação: a chave é validada no build por um gerador simples ou por teste (item 6).
3. **ViewModels**: textos derivados (títulos de pasta, cabeçalhos de data, subtítulos, mensagens de erro) passam a ser recalculados no `LanguageChanged`. A lista de mensagens é reconstruída (datas relativas mudam de formato).
4. **Remover culturas fixas**: `CultureInfo.GetCultureInfo("pt-BR")` vira `localization.FormatCulture`.
5. **Configuração**: `AppSettings.Appearance.Language` (`system` | `pt-BR` | `en` | `es`), com seletor em Aparência (e depois na tela de Configurações da fase 10). Os nomes dos idiomas aparecem sempre no próprio idioma ("English", "Español").
6. **Testes** (novo projeto `tests/Confluir.App.Tests`):
   - toda chave do `Strings.resx` existe em `en` e `es` e vice-versa;
   - os marcadores `{0}`, `{1}` são os mesmos em todas as traduções;
   - nenhum `x:Static res:Strings` sobrou no XAML;
   - formatação de datas relativas em en/es (os testes atuais do Core já cobrem pt-BR).
7. **Traduções**: en e es escritas junto com a implementação; cada fase seguinte adiciona as chaves novas nos três arquivos (o teste do item 6 impede esquecer).
8. **Fora do app**: README com uma seção "Idiomas"; textos de ajuda com links por idioma quando o provedor tiver versão localizada.

## Quando fazer

Recomendado **antes da fase 3**: hoje são ~120 chaves e a troca de `x:Static` é mecânica. Cada fase adiciona dezenas de strings, então adiar encarece a migração e a revisão de traduções.

Esforço estimado: infraestrutura + migração do XAML + testes em uma etapa; traduções en/es das chaves existentes na mesma etapa.
