# Itinerario do Pipeline TJPB (tjpdf)

Este é o pipeline **oficial** deste repositório. Não existe pipeline paralelo aqui.

## Fluxo

1) **CLI**
   - `/mnt/c/git/tjpdf/src/TjpdfPipeline.Cli/Program.cs`
   - chama `pipeline-tjpb`

2) **Leitura e análise do PDF**
   - `/mnt/c/git/tjpdf/src/TjpdfPipeline.Core/PDFAnalyzer.cs`

3) **Segmentação e extração**
   - `/mnt/c/git/tjpdf/src/TjpdfPipeline.Core/TjpbDespachoExtractor/Extraction/DespachoExtractor.cs`
   - `/mnt/c/git/tjpdf/src/TjpdfPipeline.Core/TjpbDespachoExtractor/Extraction/FieldExtractor.cs`
   - `/mnt/c/git/tjpdf/src/TjpdfPipeline.Core/TjpbDespachoExtractor/Extraction/CertidaoExtraction.cs`

4) **Regras YAML (fields)**
   - `/mnt/c/git/tjpdf/configs/fields/*.yml`

5) **Persistência**
   - `/mnt/c/git/tjpdf/src/TjpdfPipeline.Core/Utils/PgDocStore.cs`
   - salva JSON em `public.processes.json`

## Configuração

- `/mnt/c/git/tjpdf/configs/config.yaml`
- `/mnt/c/git/tjpdf/configs/fields/`
