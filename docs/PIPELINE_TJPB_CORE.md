# Mapa Encapsulado do Pipeline TJPB (TJPDF)

Este documento é o **mapa curto e canônico** do pipeline. Ele existe para que o time nunca perca o caminho do fluxo nem os arquivos-chave.

> Detalhes completos e passo-a-passo: `docs/PIPELINE_TJPB_FLOW.md`  
> Status e pendências: `docs/PIPELINE_TJPB_STATUS.md`

---

## Objetivo do repositório

Extrair **fields estruturados** (despacho, certidão CM e requerimento) com critérios rígidos, gerar JSON confiável por documento e persistir no Postgres.

---

## Campos principais (ordem final para exportação)

1) **PROCESSO_ADMINISTRATIVO**  
2) **PROCESSO_JUDICIAL**  
3) **COMARCA**  
4) **VARA**  
5) **PROMOVENTE** *(autor do processo judicial)*  
6) **PROMOVIDO** *(réu do processo judicial)*  
7) **PERITO**  
8) **CPF_PERITO**  
9) **ESPECIALIDADE**  
10) **ESPECIE_DA_PERICIA**  
11) **VALOR_ARBITRADO_JZ**  
12) **VALOR_ARBITRADO_DE**  
13) **VALOR_ARBITRADO_CM**  
14) **VALOR_ARBITRADO_FINAL** *(não é soma)*  
15) **DATA_ARBITRADO_FINAL**

**Regra do valor arbitrado final**:
- Se houver **VALOR_ARBITRADO_CM**, ele é o final e a data é a **data da decisão do Conselho** (certidão CM).
- Se não houver CM, usar **VALOR_ARBITRADO_DE** e a data do **despacho**.
- Se não houver DE nem CM, usar **VALOR_ARBITRADO_JZ** e a data do **despacho/requerimento**.

---

## Diagrama (macro)

```
[S1 Input Dir]
   |
   v
[S1 Preprocess ZIP?]
   |-- yes --> [S1a Merge PDFs + Bookmarks] --> [S1b Process Folder + PDF]
   |-- no  ------------------------------------> [S1b Process Folder + PDF]
                     |
                     v
             [S2 PDFAnalyzer]
                     |
                     v
           [S3 Try Bookmarks]
                 |
                 v
        {S3? Bookmarks encontrados?}
           |               |
          sim             nao
           |               |
           v               v
 [S4 Boundaries (bookmarks)] [S5 DocumentSegmenter]
           |               |
           +-------> [S6 Document Boundaries]
                     |
                     v
     [S7 BuildDocObject (DTO por documento)]
          |   - monta chaves de header/footer/origem/assinatura no DTO
          |   - define doc_label/doc_type
          |   - valida despacho/certidão/requerimento
          v
   [S8 Field Extraction (YAML + Directed + Specialized)]
                     |
                     v
             [S9 JSON per Document]
                     |
                     v
               [S10 Postgres Persist]
```

---

## Até o DTO (ordem real)

1) **[S3→S6] Bookmarks → Boundaries**
   - `BuildBookmarkBoundaries` cria `DocumentBoundary`.
   - **Sanitiza o título** com `SanitizeBookmarkTitle` (antes do DTO).

2) **[S7] BuildDocObject (DTO)**
   - monta chaves de header/footer/origem/assinatura diretamente no DTO.
   - define `doc_label` e `doc_type`.
   - identifica **despacho / certidão / requerimento**.

3) **[S8] Extração de fields**
   - YAML + heurísticas dirigidas + extratores especializados (mesma etapa).

---

## Núcleos do pipeline (o que faz + onde está)

**S1** **Ingestão / Pré-processamento (ZIP)**
   - **Função**: detectar ZIP, descompactar, mergear PDFs, criar bookmarks por arquivo.
   - **Saídas**: `<processo>.pdf` + lista de origem (`<processo>_arquivos.txt`).
   - **Arquivos**:
     - `src/TjpdfPipeline.Cli/Commands/PreprocessInputsCommand.cs`
     - `src/TjpdfPipeline.Cli/Commands/PipelineTjpbCommand.cs` (PreprocessZipInbox)

**S2** **Análise do PDF (base de tudo)**
   - **Função**: texto, palavras/coords, headers/footers, bookmarks, assinaturas.
   - **Saída**: `PDFAnalysisResult` + `BookmarksFlat`.
   - **Arquivo**:
     - `src/TjpdfPipeline.Core/PDFAnalyzer.cs`

**S3–S6** **Segmentação de documentos**
   - **Função**: criar boundaries por bookmark; fallback heurístico se não houver.
   - **Arquivos**:
     - `src/TjpdfPipeline.Cli/Commands/PipelineTjpbCommand.cs` (BuildBookmarkBoundaries)
     - `src/TjpdfPipeline.Core/Utils/DocumentSegmenter.cs` (fallback)
     - `src/TjpdfPipeline.Core/Models/DocumentSegmentationConfig.cs` (config do segmenter)

**S7** **DTO + seleção do documento (despacho/certidão/requerimento)**
   - **Função**: sanitizar títulos, aplicar critérios (páginas, origem, assinatura, densidade).
   - **Arquivos**:
     - `src/TjpdfPipeline.Cli/Commands/PipelineTjpbCommand.cs` (SanitizeBookmarkTitle, MatchesOrigin, MatchesSigner)
     - `configs/config.yaml` (anchors, thresholds, signerHints)

**S8** **Extração de campos**
   - **Função**: executar scripts e heurísticas dirigidas.
   - **Arquivos**:
     - `configs/fields/*.yml`
     - `src/TjpdfPipeline.Core/Utils/FieldScripts.cs`
     - `src/TjpdfPipeline.Cli/Commands/PipelineTjpbCommand.cs` (ExtractFields / ExtractDirectedValues)

**S8a** **Extração especializada (despacho/certidão)**
   - **Função**: regras portadas do pipeline antigo.
   - **Arquivos**:
     - `src/TjpdfPipeline.Core/TjpbDespachoExtractor/Extraction/DespachoExtractor.cs`
     - `src/TjpdfPipeline.Core/TjpbDespachoExtractor/Extraction/FieldExtractor.cs`
     - `src/TjpdfPipeline.Core/TjpbDespachoExtractor/Extraction/CertidaoExtraction.cs`

**S8b** **Referências / Catálogos**
   - **Função**: normalização e enriquecimento de campos.
   - **Arquivos**:
     - `src/PipelineTjpb/reference/peritos/*`
     - `src/PipelineTjpb/reference/valores/*`
     - `src/PipelineTjpb/reference/laudos_hashes/*`
     - `src/PipelineTjpb/reference/laudos/laudos_por_especie*.csv`

**S9** **JSON por documento**
   - **Função**: payload final do documento (DTO + fields + forensics).

**S10** **Persistência**
   - **Função**: gravar JSON no Postgres (processes.json).
   - **Arquivo**:
     - `src/TjpdfPipeline.Core/Utils/PgDocStore.cs`

---

## Entradas e saídas oficiais

- **Entrada principal**: `pipeline-tjpb --input-dir <dir>`
- **Saída principal**: JSON por processo/documento persistido no Postgres.
- **Sem geração intermediária** por padrão.

---

## Comandos auxiliares (não fazem parte do pipeline)

Usados só para inspeção/debug:

- `fetch-bookmark-titles`
- `bookmark-paragraphs`
- `footer`
- `pdf-objects`, `pdf-info`, `pdf-streams`, `pdf-unicode`
- `load`

---

## Regra de ouro

Este repositório tem **um único pipeline oficial**.  
Qualquer mudança precisa atualizar este mapa.
