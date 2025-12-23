# TJPB Pipeline (tjpdf)

CLI e core isolados para o pipeline do TJPB. Este repositório existe para evitar confusão com outros pipelines e manter build próprio.

## Como rodar

```bash
# exemplo
cd /mnt/c/git/tjpdf

dotnet run --project /mnt/c/git/tjpdf/src/TjpdfPipeline.Cli -- pipeline-tjpb --input-dir /caminho/para/pdfs --config /mnt/c/git/tjpdf/configs/config.yaml

# exportação CSV (por processo)
dotnet run --project /mnt/c/git/tjpdf/src/TjpdfPipeline.Cli -- pipeline-tjpb --input-dir /caminho/para/pdfs --export-csv /caminho/saida.csv
```

## Bookmarks (títulos + páginas)

```bash
dotnet run --project /mnt/c/git/tjpdf/src/TjpdfPipeline.Cli -- fetch-bookmark-titles --input-file /caminho/arquivo.pdf
```

## Configuração

- `/mnt/c/git/tjpdf/configs/config.yaml` (principal)
- `/mnt/c/git/tjpdf/configs/fields/*.yml` (regras por campo)
- Changelog oficial: `/mnt/c/git/tjpdf/docs/CHANGELOG_PIPELINE_TJPB.md`
- Mapa encapsulado do pipeline: `/mnt/c/git/tjpdf/docs/PIPELINE_TJPB_CORE.md`

## Saída

A saída padrão é **Postgres** (tabela `processes`, coluna `json`). Não gera arquivos por padrão.
Para exportação local, use `--export-csv`.
O extrator de despacho gera documentos `despacho` e `certidao_cm` na mesma hierarquia.

## Estrutura

- `/mnt/c/git/tjpdf/src/TjpdfPipeline.Core/` — core do pipeline (análise, extração, persistência)
- `/mnt/c/git/tjpdf/src/TjpdfPipeline.Cli/` — CLI dedicado
- `/mnt/c/git/tjpdf/configs/` — configs e regras YAML
- `/mnt/c/git/tjpdf/docs/` — documentação operacional
