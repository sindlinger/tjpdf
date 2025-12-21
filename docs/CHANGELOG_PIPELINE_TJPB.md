# Changelog – Pipeline TJPB

Este arquivo é a **fonte única** de mudanças do pipeline TJPB.
Toda alteração de **extração de campos**, **estrutura do JSON** ou **regras/configs** deve ser registrada aqui.

## 2025-12-21

### Alterações
- Mapeamento inicial das fontes de campos (YAML + extractor + fallback) para despacho/certidão.
- PROCESSO_JUDICIAL: fallback via CNJ em `process_cnj` no `doc_summary` (preenche quando CNJ aparece no texto).
- VARA/COMARCA: extração de “Juízo/Comarca” aceita linhas não iniciadas por “Juízo”, pega comarca na linha seguinte quando há quebra.
- VARA/COMARCA: limpeza para remover “da/do/de” final em VARA e pontuação/espaçamento em COMARCA.
- Scripts YAML `processo_partes.yml` e `juizo_comarca.yml` agora rodam para bucket `principal` sem exigir `name_matches`.
- JSON do documento: `fields_missing` passa a registrar campos obrigatórios ausentes.
- `--print-json` no comando `pipeline-tjpb` para inspecionar o JSON completo sem Postgres.
- BBox em fields (quando possível) e `bbox=null` padronizado quando ausente.
- `arbitrados.yml`: novos padrões para honorários fixados e “valor arbitrado” pós‑valor; bucket liberado para `principal`, `laudo` e `outro`.
- `arbitrados.yml`: padrão para “reserva ... no valor de R$” agora aceita quebra de linha.

### Observações
- O pipeline atual já usa `configs/fields/*.yml`, `configs/config.yaml` e referências em `src/PipelineTjpb/reference/*`.
- Ajustes futuros devem priorizar **regras de extração** (YAML + templates) antes de trazer código extra do repo antigo.
