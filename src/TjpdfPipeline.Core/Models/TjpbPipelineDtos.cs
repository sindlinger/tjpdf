using System.Collections.Generic;
using Newtonsoft.Json;

namespace FilterPDF.Models
{
    public class TjpbProcessDto
    {
        [JsonProperty("process")]
        public string Process { get; set; } = "";

        [JsonProperty("pdf")]
        public Dictionary<string, object> Pdf { get; set; } = new();

        [JsonProperty("documents")]
        public List<TjpbDocumentDto> Documents { get; set; } = new();

        [JsonProperty("paragraph_stats")]
        public List<object> ParagraphStats { get; set; } = new();
    }

    public class TjpbDocumentDto
    {
        [JsonProperty("process")]
        public string Process { get; set; } = "";

        [JsonProperty("pdf_path")]
        public string PdfPath { get; set; } = "";

        [JsonProperty("doc_label")]
        public string DocLabel { get; set; } = "";

        [JsonProperty("doc_label_original")]
        public string DocLabelOriginal { get; set; } = "";

        [JsonProperty("doc_type")]
        public string DocType { get; set; } = "";

        [JsonProperty("start_page")]
        public int StartPage { get; set; }

        [JsonProperty("end_page")]
        public int EndPage { get; set; }

        [JsonProperty("doc_pages")]
        public int DocPages { get; set; }

        [JsonProperty("total_pages")]
        public int TotalPages { get; set; }

        [JsonProperty("word_count")]
        public long WordCount { get; set; }

        [JsonProperty("char_count")]
        public long CharCount { get; set; }

        [JsonProperty("percentual_blank")]
        public double PercentualBlank { get; set; }

        [JsonProperty("header")]
        public string Header { get; set; } = "";

        [JsonProperty("footer")]
        public string Footer { get; set; } = "";

        [JsonProperty("footer_signature_raw")]
        public string FooterSignatureRaw { get; set; } = "";

        [JsonProperty("is_despacho")]
        public bool IsDespacho { get; set; }

        [JsonProperty("is_certidao")]
        public bool IsCertidao { get; set; }

        [JsonProperty("is_requerimento_pagamento_honorarios")]
        public bool IsRequerimentoPagamentoHonorarios { get; set; }

        [JsonProperty("laudo_hash")]
        public string LaudoHash { get; set; } = "";

        [JsonProperty("hash_db_match")]
        public bool HashDbMatch { get; set; }

        [JsonProperty("hash_db_path")]
        public string HashDbPath { get; set; } = "";

        [JsonProperty("hash_db_especie")]
        public string HashDbEspecie { get; set; } = "";

        [JsonProperty("hash_db_natureza")]
        public string HashDbNatureza { get; set; } = "";

        [JsonProperty("hash_db_autor")]
        public string HashDbAutor { get; set; } = "";

        [JsonProperty("hash_db_arquivo")]
        public string HashDbArquivo { get; set; } = "";

        [JsonProperty("hash_db_quesitos")]
        public string HashDbQuesitos { get; set; } = "";

        [JsonProperty("origin_main")]
        public string OriginMain { get; set; } = "";

        [JsonProperty("origin_sub")]
        public string OriginSub { get; set; } = "";

        [JsonProperty("origin_extra")]
        public string OriginExtra { get; set; } = "";

        [JsonProperty("signer")]
        public string Signer { get; set; } = "";

        [JsonProperty("signed_at")]
        public string SignedAt { get; set; } = "";

        [JsonProperty("header_hash")]
        public string HeaderHash { get; set; } = "";

        [JsonProperty("title")]
        public string Title { get; set; } = "";

        [JsonProperty("template")]
        public string Template { get; set; } = "";

        [JsonProperty("sei_process")]
        public string SeiProcess { get; set; } = "";

        [JsonProperty("sei_doc")]
        public string SeiDoc { get; set; } = "";

        [JsonProperty("sei_crc")]
        public string SeiCrc { get; set; } = "";

        [JsonProperty("sei_verifier")]
        public string SeiVerifier { get; set; } = "";

        [JsonProperty("auth_url")]
        public string AuthUrl { get; set; } = "";

        [JsonProperty("date_footer")]
        public string DateFooter { get; set; } = "";

        [JsonProperty("doc_head")]
        public string DocHead { get; set; } = "";

        [JsonProperty("doc_tail")]
        public string DocTail { get; set; } = "";

        [JsonProperty("doc_head_bbox_text")]
        public string DocHeadBboxText { get; set; } = "";

        [JsonProperty("doc_head_bbox")]
        public Dictionary<string, object> DocHeadBbox { get; set; } = new();

        [JsonProperty("doc_tail_bbox_text")]
        public string DocTailBboxText { get; set; } = "";

        [JsonProperty("doc_tail_bbox")]
        public Dictionary<string, object> DocTailBbox { get; set; } = new();

        [JsonProperty("process_line")]
        public string ProcessLine { get; set; } = "";

        [JsonProperty("process_bbox")]
        public Dictionary<string, object> ProcessBbox { get; set; } = new();

        [JsonProperty("interested_line")]
        public string InterestedLine { get; set; } = "";

        [JsonProperty("interested_name")]
        public string InterestedName { get; set; } = "";

        [JsonProperty("interested_profession")]
        public string InterestedProfession { get; set; } = "";

        [JsonProperty("interested_email")]
        public string InterestedEmail { get; set; } = "";

        [JsonProperty("juizo_line")]
        public string JuizoLine { get; set; } = "";

        [JsonProperty("juizo_vara")]
        public string JuizoVara { get; set; } = "";

        [JsonProperty("comarca")]
        public string Comarca { get; set; } = "";

        [JsonProperty("interested_bbox")]
        public Dictionary<string, object> InterestedBbox { get; set; } = new();

        [JsonProperty("juizo_bbox")]
        public Dictionary<string, object> JuizoBbox { get; set; } = new();

        [JsonProperty("certidao_date")]
        public string CertidaoDate { get; set; } = "";

        [JsonProperty("despacho_date")]
        public string DespachoDate { get; set; } = "";

        [JsonProperty("forensics")]
        public Dictionary<string, object> Forensics { get; set; } = new();

        [JsonProperty("fields")]
        public List<Dictionary<string, object>> Fields { get; set; } = new();

        [JsonProperty("band_fields")]
        public List<Dictionary<string, object>> BandFields { get; set; } = new();

        [JsonProperty("bookmarks")]
        public List<Dictionary<string, object>> Bookmarks { get; set; } = new();

        [JsonProperty("anexos_bookmarks")]
        public List<Dictionary<string, object>> AnexosBookmarks { get; set; } = new();
    }
}
