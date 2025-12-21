# Status do Pipeline TJPB (TJPDF)

Atualizado em: **2025-12-21 05:20:55 -03**

## Contexto e objetivo (assuma este repositório a partir de agora)

Estamos reconstruindo o pipeline TJPB no repositório atual **/mnt/c/git/tjpdf**. O objetivo central do projeto é **extrair campos estruturados (“fields”) com critérios rígidos**, sem adivinhação, e gerar **JSON confiável por processo/documento**, persistindo no Postgres (tabela `processes`, coluna `json`). Não há geração de arquivos intermediários por padrão.

Havia um repositório antigo mais completo (**/mnt/b/dev/sei-dashboard/fpdf-portable**), onde a ingestão, bookmarks e outras ferramentas estavam mais maduras. Parte dessa lógica foi portada, mas ainda precisamos garantir que o pipeline atual replique os comportamentos essenciais, especialmente **ingestão/merge e bookmarks**, pois a segmentação depende deles.

---

## O que já foi feito aqui no TJPDF

### 1) Bookmarks / Segmentação
- Implementado comando de bookmarks (CLI) e estrutura `BookmarksFlat`.
- Corrigido o cálculo de página do bookmark usando **NameTree + DestinationPage**, com fallback para raw outlines e named dests.
- Criado comando auxiliar para debug:
  - `fetch-bookmark-titles`

### 2) Pré-processamento de ZIP
- Para PDFs que chegam via ZIP: **descompacta, mergeia, cria bookmarks por nome de arquivo**.
- Para PDFs soltos: copia/normaliza.
- **Novo comando**: `preprocess-inputs`
  - Saída padrão: **uma pasta por processo** com `<processo>.pdf`
  - Título do PDF = **número do processo**
  - Gera **arquivo de texto** na raiz do out-dir com a lista ordenada dos arquivos que viraram bookmarks.
  - Exemplo: `out-dir/123456_arquivos.txt`
    ```
    1. nome_do_primeiro_pdf
    2. nome_do_segundo_pdf
    ...
    ```

### 3) Footer / Assinatura
- Criado comando `footer` para inspecionar assinatura e datas.
- Melhorada extração de **signer, signed_at e footer_signature_raw**.
- Essas informações entram no JSON no nível do documento (origin_*, signer, signed_at, date_footer etc.).

### 4) Forense / Parágrafos / Bands
- O pipeline já inclui `forensics` com:
  - `paragraphs`
  - `bands` (header/subheader/body/footer)
  - `anchors`, `repeat_lines`, `outlier_lines`
  - `font_dominant`, `size_dominant`
- Cada documento tem métricas como:
  - `doc_pages`, `total_pages`
  - `text_density`, `blank_ratio`
  - `fonts` e `words` (com fonte/tamanho por palavra)

---

## Objetivo atual

Consolidar a reconstrução do pipeline com foco em **extração de fields** (despacho, certidão CM e requerimento de pagamento de honorários), garantindo:

- Segmentação correta via bookmarks (originais ou gerados no preprocess).
- Critérios rígidos de validação (sem “chutar”):
  - despacho desejado >= 2 páginas, origem específica, assinatura específica.
  - certidão desejada com origem e assinatura específicas.
  - requerimento com nome/título específico.
- Construção de JSON estruturado com:
  - campos de cabeçalho/assinatura no nível do documento (origin_*, signer, signed_at, date_footer, etc.)
  - forensics
  - paragraphs
  - bands
  - fields extraídos (inclusive via YAML)
- Persistência no Postgres (processes.json).

### Fields principais (alvo imediato)

- `processo_administrativo`
- `processo_judicial`
- `perito`
- `perito_cpf`
- `especialidade`
- `especie_pericia`
- `valor_arbitrado_jz` (requerimento do juiz)
- `valor_arbitrado_de` (despacho DE / Diretoria Especial)
- `valor_arbitrado_cm` (certidão CM / Conselho da Magistratura)
- `valor_arbitrado_geral` (soma de JZ+DE+CM quando disponíveis)
- `comarca`
- `vara`

---

## O que ainda precisa ser feito

1) **Golden JSON**
- Gerar um “golden” confiável a partir de um processo real para validar fields.
- Servirá como baseline de comparação.

2) **Extratores YAML**
- Garantir que todos os scripts e extratores (YAML) estejam atualizados e rodando.
- Integrar com as métricas de forense e campos de cabeçalho/assinatura.

3) **Diferenças / Validação**
- Usar `paragraphs`, `bands`, `summary` e `forensics` para identificar divergências.
- Criar “diffs” confiáveis entre outputs atuais e o esperado.

4) **Revisar lógica herdada do repositório antigo**
- Extratores e comandos que ainda não foram portados.
- Ajustar comportamentos inconsistentes entre o antigo e o atual.

---

## Observação final

A partir deste momento, **assuma /mnt/c/git/tjpdf como repositório principal no Codex**.  
Todas as próximas ações devem partir daqui, e o foco é **seguir até o final do pipeline**, garantindo extração correta dos campos.

---

## Como pedir mudanças (template de prompt)

Para acelerar e reduzir retrabalho, use este template ao solicitar novas tarefas:

```
Objetivo:
Escopo:
Não-escopo:
Entradas (paths):
Saídas esperadas:
Critérios de aceitação:
Restrições:
Comandos de teste:
Fallback:
```

## Prompt preenchido (base atual)

```
Objetivo:
Extrair fields (campos) estruturados do pipeline TJPB com critérios rígidos, gerar JSON por processo/documento e persistir no Postgres (processes.json), sem “chutar”.

Escopo:
- Repositório atual: /mnt/c/git/tjpdf (baseline).
- Pré-processamento (ZIP -> merge + bookmarks por arquivo; PDF solto -> copia/normaliza).
- Segmentação por bookmarks.
- Enriquecimento do JSON com campos de cabeçalho/assinatura (top-level), forensics, paragraphs, bands.
- Extratores YAML e validações rígidas (despacho, certidão CM, requerimento).
- Fields alvo: processo administrativo/judicial, perito (nome/CPF), especialidade, espécie, comarca, vara, valores arbitrados (JZ/DE/CM) e total.

Não-escopo:
- Alterar regras de negócio sem validação.
- Criar campos “adivinhados”.
- Mudar pipeline principal sem alinhamento.

Entradas (paths):
- PDFs/ZIPs de entrada (input-dir).
- configs/fields (YAML).
- configs/config.yaml (opcional).
- /mnt/b/dev/sei-dashboard/fpdf-portable (referência antiga).

Saídas esperadas:
- JSON estruturado por processo/documento.
- Persistência no Postgres (processes.json).
- Arquivo texto de lista de PDFs do ZIP: <processo>_arquivos.txt na raiz do out-dir.

Critérios de aceitação:
- Bookmarks corretos (originais ou gerados no merge de ZIP).
- Despacho válido: >= 2 páginas + origem + assinatura específicas.
- Certidão CM válida: origem + assinatura específicas.
- Requerimento identificado por título específico.
- JSON com campos de cabeçalho/assinatura (top-level) + forensics + paragraphs + bands + fields.

Restrições:
- Sem “chutar” campos.
- Não alterar pipeline principal sem discutir.

Comandos de teste:
- fetch-bookmark-titles
- footer
- preprocess-inputs
- pipeline-tjpb (com --debug-docsummary quando necessário)

Fallback:
- ZIP sem PDFs úteis: logar e pular.
- PDF solto sem bookmark: quarentena (ou tratar como doc único somente se passar critérios rígidos).
```
