# Changelog – Pipeline TJPB

Este arquivo é a **fonte única** de mudanças do pipeline TJPB.
Toda alteração de **extração de campos**, **estrutura do JSON** ou **regras/configs** deve ser registrada aqui.

## 2025-12-21

### Alterações
- Mapeamento inicial das fontes de campos (YAML + extractor + fallback) para despacho/certidão.
- PROCESSO_JUDICIAL: fallback via CNJ em `process_cnj` no DTO (preenche quando CNJ aparece no texto).
- VARA/COMARCA: extração de “Juízo/Comarca” aceita linhas não iniciadas por “Juízo”, pega comarca na linha seguinte quando há quebra.
- VARA/COMARCA: limpeza para remover “da/do/de” final em VARA e pontuação/espaçamento em COMARCA.
- Scripts YAML `processo_partes.yml` e `juizo_comarca.yml` agora rodam para bucket `principal` sem exigir `name_matches`.
- JSON do documento: `fields_missing` passa a registrar campos obrigatórios ausentes.
- `--print-json` no comando `pipeline-tjpb` para inspecionar o JSON completo sem Postgres.
- BBox em fields (quando possível) e `bbox=null` padronizado quando ausente.
- `arbitrados.yml`: novos padrões para honorários fixados e “valor arbitrado” pós‑valor; bucket liberado para `principal`, `laudo` e `outro`.
- `arbitrados.yml`: padrão para “reserva ... no valor de R$” agora aceita quebra de linha.
- Documento `PIPELINE_TJPB_FLOW.md` atualizado com etapa de seleção de documentos (bookmark + sanitização + DT-OR).
- Novo comando `bookmark-paragraphs` para slices de primeiro/último parágrafo por bookmark.
- `fetch-bookmark-titles` agora mostra a página inicial do bookmark no modo texto.
- `doc_head` e `doc_tail` adicionados ao JSON (top-level) para capturar inicio e fim do documento.
- `PIPELINE_TJPB_FLOW.md` detalhado com arquivos/funções por etapa.
- Removidos do JSON: `despacho_tipo`, `despacho_categoria`, `despacho_destino`.
- Mantidas apenas as flags finais: `is_despacho_autorizacao` e `is_despacho_encaminhamento` (GEORC = autorização; Conselho = encaminhamento).
- `is_despacho_valid` agora exige `percentual_blank <= blank_max_pct` além de `minPages` e hints (config.yaml).
- Removidos hints genéricos de autorização: `AUTORIZADO` e `AUTORIZA` (config.yaml).
- Header fallback: quando não há header nativo, o pipeline usa linhas do topo (LineInfo), depois words/band, e por fim as 3 primeiras linhas do PageText.

### Observações
- O pipeline atual já usa `configs/fields/*.yml`, `configs/config.yaml` e referências em `src/PipelineTjpb/reference/*`.
- Ajustes futuros devem priorizar **regras de extração** (YAML + templates) antes de trazer código extra do repo antigo.

## 2025-12-23

### Alterações
- Rodapé/assinatura: `footer_signature_raw` agora é reconstruído também pelo **band de rodapé** usando words/coords (não só `lastPageText`).
- Assinatura/data: `signer`, `signed_at` e `date_footer` passam a considerar **texto colapsado** (letras espaçadas) e fallback compacto.
- Normalização de nomes: restaura espaços em nomes colapsados (inclui `de/da/do/dos/das` + separação de camelcase).
- Validação rápida em amostras: despacho/certidão/requerimento agora preenchem `signer` e `signed_at` quando há rodapé SEI.
- `fields_missing` passou a ser **por tipo de documento** (despacho/certidão/requerimento), evitando exigir campos que não pertencem ao documento.
- `config.yaml`: catálogo de peritos ampliado (`peritos_catalogo.csv` e `peritos_catalogo_parquet.csv`).
- Fallback de `ESPECIE_DA_PERICIA`: usa referência `laudos_por_especie_*.csv` quando não há match de honorários.
- `pipeline-tjpb`: nova opção `--export-csv` para gerar CSV consolidado por processo (campos principais).
- CSV (campos principais): adicionados **PROMOVENTE/PROMOVIDO** e regra de **VALOR_ARBITRADO_FINAL/DATA_ARBITRADO_FINAL** (não é soma).
- Comandos de etapa adicionados: `tjpb-s1` e `tjpb-s3` (validação progressiva por etapa).
- PDFAnalyzer + fetch-bookmark-titles: agora compartilham `BookmarkExtractor` (Outlines + fallback `/Outlines` + NameTree).
- PDFAnalyzer: tabulação do `PageText` agora colapsa letras espaçadas (ex.: “P O D E R” → “PODER”) preservando separação entre palavras.
- Assinatura: evita falso positivo (“Assinatura” sem nome) e faz fallback de `signer` buscando em todo o texto do documento.
- Assinatura: nova ordem de preferência (digital → SEI → rodapé → fulltext → lastline), e `lastline` desativado para anexos.
- `pipeline-tjpb`: flag `--debug-signer` adiciona `signer_candidates` no JSON.
- PDFAnalyzer: texto tabulado mantém fallback para texto extraído (não zera quando a tabulação falha).

## 2025-12-24

### Alterações
- **AnchorTemplateExtractor** integrado para extração por âncoras no despacho (head/tail) usando templates anotados.
- **Templates de despacho** adicionados em `configs/anchor_templates/`:
  - `tjpb_despacho_head_nlp_input.txt` (insumo bruto)
  - `tjpb_despacho_tail_nlp_input.txt` (insumo bruto)
  - `tjpb_despacho_head_annotated.txt` (esperado pela pipeline quando existir)
  - `tjpb_despacho_tail_annotated.txt` (esperado pela pipeline quando existir)
- DTO por documento:
  - `doc_head_bbox_text` / `doc_head_bbox` (início do despacho → fim do 1º parágrafo).
  - `doc_tail_bbox_text` / `doc_tail_bbox` (último parágrafo → fim do documento).
  - `despacho_head_anchor_fields` / `despacho_tail_anchor_fields` com resultados da extração por template.
  - `despacho_head_template_path` / `despacho_tail_template_path` para rastreio do template usado.
- Regras de aplicação do template:
  - **somente** no despacho validado,
  - **somente** no despacho‑alvo (DIESP/Robson),
  - **apenas uma vez** por processo.
- `is_despacho_target` introduzido (detecção por conteúdo: Diretoria Especial + Robson + despacho reserva/GEORC).
- `percentual_blank` passou a ser **critério relativo de desempate** (não mais condição rígida).
- Novo campo **DATA_REQUISICAO**:
  - extraído no requerimento de pagamento de honorários,
  - gravado no DTO do requerimento,
  - exportado no CSV consolidado.
- Novo comando de etapa: `tjpb-s7` (segmentação + BuildDocObject) com saída `stage7_outputs.json`.
