# Itinerario do Pipeline TJPB (tjpdf)

Este é o pipeline **oficial** deste repositório. Não existe pipeline paralelo aqui.

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

## Fluxo (pipeline oficial)

1) **CLI**
   - `/mnt/c/git/tjpdf/src/TjpdfPipeline.Cli/Program.cs`
   - chama `pipeline-tjpb`

2) **Pré-processamento (quando há ZIP)**
   - `pipeline-tjpb` chama `PreprocessZipInbox(...)` internamente
   - ou CLI dedicado: `preprocess-inputs`
   - mergeia PDFs e cria bookmarks por arquivo (nome do arquivo vira título)

3) **Leitura e análise do PDF**
   - `/mnt/c/git/tjpdf/src/TjpdfPipeline.Core/PDFAnalyzer.cs`

4) **Segmentação por bookmarks (fallback heurístico se não houver)**
   - `BuildBookmarkBoundaries(...)` (preferencial)
   - `DocumentSegmenter.FindDocuments(...)` (fallback)

5) **Extração e construção do JSON**
   - YAML (`configs/fields/*.yml`)
   - Extratores dirigidos (pipeline)
   - Extrator portado (despacho/certidão)
   - Requerimento (extrator dedicado)

6) **Persistência**
   - `PgDocStore.UpsertProcess(...)` grava JSON em `public.processes.json`

## Núcleos do pipeline (visão encapsulada)

1) **Ingestão / Pré-processamento**
   - Responsável: detectar ZIP, mergear PDFs, criar bookmarks por arquivo.
   - Arquivos:
     - `src/TjpdfPipeline.Cli/Commands/PreprocessInputsCommand.cs`
     - `src/TjpdfPipeline.Cli/Commands/PipelineTjpbCommand.cs` (PreprocessZipInbox)

2) **Análise do PDF**
   - Responsável: texto, palavras, headers/footers, bookmarks, assinaturas.
   - Arquivos:
     - `src/TjpdfPipeline.Core/PDFAnalyzer.cs`

3) **Segmentação de documentos**
   - Responsável: criar boundaries por bookmark (preferencial) ou heurística.
   - Arquivos:
     - `src/TjpdfPipeline.Cli/Commands/PipelineTjpbCommand.cs` (BuildBookmarkBoundaries)
     - `src/TjpdfPipeline.Core/Utils/DocumentSegmenter.cs` (fallback)

4) **Extração de campos (YAML + dirigidos)**
   - Responsável: rodar scripts `configs/fields/*.yml` + heurísticas dirigidas.
   - Arquivos:
     - `src/TjpdfPipeline.Core/Utils/FieldScripts.cs`
     - `configs/fields/*.yml`
     - `src/TjpdfPipeline.Cli/Commands/PipelineTjpbCommand.cs` (ExtractFields / ExtractDirectedValues)

5) **Extração especializada (Despacho/Certidão/Requerimento)**
   - Responsável: regras portadas + extrator dedicado.
   - Arquivos:
     - `src/TjpdfPipeline.Core/TjpbDespachoExtractor/Extraction/DespachoExtractor.cs`
     - `src/TjpdfPipeline.Core/TjpbDespachoExtractor/Extraction/FieldExtractor.cs`
     - `src/TjpdfPipeline.Core/TjpbDespachoExtractor/Extraction/CertidaoExtraction.cs`
     - `src/TjpdfPipeline.Cli/Commands/PipelineTjpbCommand.cs` (requerimento)

6) **Referências (catálogos)**
   - Responsável: enriquecimento/normalização de campos.
   - Arquivos:
     - `src/PipelineTjpb/reference/peritos/*`
     - `src/PipelineTjpb/reference/valores/*`
     - `src/PipelineTjpb/reference/laudos_hashes/*`
     - `src/PipelineTjpb/reference/laudos/laudos_por_especie*.csv` (fallback espécie)

7) **Persistência**
   - Responsável: gravar JSON no Postgres.
   - Arquivo:
     - `src/TjpdfPipeline.Core/Utils/PgDocStore.cs`

## Arquivos usados diretamente pelo pipeline

- `/mnt/c/git/tjpdf/src/TjpdfPipeline.Cli/Commands/PipelineTjpbCommand.cs`
- `/mnt/c/git/tjpdf/src/TjpdfPipeline.Cli/Commands/PreprocessInputsCommand.cs` (quando há ZIP)
- `/mnt/c/git/tjpdf/src/TjpdfPipeline.Core/PDFAnalyzer.cs`
- `/mnt/c/git/tjpdf/src/TjpdfPipeline.Core/Utils/DocumentSegmenter.cs`
- `/mnt/c/git/tjpdf/src/TjpdfPipeline.Core/TjpbDespachoExtractor/Extraction/DespachoExtractor.cs`
- `/mnt/c/git/tjpdf/src/TjpdfPipeline.Core/TjpbDespachoExtractor/Extraction/FieldExtractor.cs`
- `/mnt/c/git/tjpdf/src/TjpdfPipeline.Core/TjpbDespachoExtractor/Extraction/CertidaoExtraction.cs`
- `/mnt/c/git/tjpdf/configs/fields/*.yml`
- `/mnt/c/git/tjpdf/configs/config.yaml`
- `/mnt/c/git/tjpdf/src/TjpdfPipeline.Core/Utils/PgDocStore.cs`

## Comandos auxiliares (diagnóstico – não são pipeline)

Esses comandos **não fazem parte do fluxo principal** e existem apenas para inspeção/debug:

- `load` → `/mnt/c/git/tjpdf/src/TjpdfPipeline.Cli/Commands/FpdfLoadCommand.cs`
- `pdf-objects` → `/mnt/c/git/tjpdf/src/TjpdfPipeline.Cli/Commands/Analysis/PdfObjectsCommand.cs`
- `pdf-info` → `/mnt/c/git/tjpdf/src/TjpdfPipeline.Cli/Commands/Analysis/PdfInfoCommand.cs`
- `pdf-streams` → `/mnt/c/git/tjpdf/src/TjpdfPipeline.Cli/Commands/Analysis/PdfStreamsCommand.cs`
- `pdf-unicode` → `/mnt/c/git/tjpdf/src/TjpdfPipeline.Cli/Commands/Analysis/PdfUnicodeCommand.cs`
- `show-moddate` → `/mnt/c/git/tjpdf/src/TjpdfPipeline.Cli/Commands/Analysis/ShowModDateCommand.cs`
- `fetch-bookmark-titles` → `/mnt/c/git/tjpdf/src/TjpdfPipeline.Cli/Commands/Analysis/FetchBookmarkTitlesCommand.cs`
- `bookmark-paragraphs` → `/mnt/c/git/tjpdf/src/TjpdfPipeline.Cli/Commands/Analysis/BookmarkParagraphsCommand.cs`
- `footer` → `/mnt/c/git/tjpdf/src/TjpdfPipeline.Cli/Commands/Analysis/FooterCommand.cs`

## Configuração

- `/mnt/c/git/tjpdf/configs/config.yaml`
- `/mnt/c/git/tjpdf/configs/fields/`
