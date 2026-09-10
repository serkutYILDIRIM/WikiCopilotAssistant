using System.ComponentModel.DataAnnotations;

namespace WikiCopilotAssistant.Models;

public sealed class ChatPageModel
{
    [Required(ErrorMessage = "Kaynak URL'sini girin.")]
    [StringLength(4096, ErrorMessage = "Kaynak URL'si en fazla 4096 karakter olabilir.")]
    [Display(Name = "Kaynak URL'si")]
    public string SourceUrl { get; set; } = "";

    [Required(ErrorMessage = "Sorunuzu yazın.")]
    [StringLength(8000, ErrorMessage = "Sorunuz en fazla 8000 karakter olabilir.")]
    [Display(Name = "Sorunuz")]
    public string Question { get; set; } = "";

    public CopilotStatus Connection { get; set; } = new("checking", "Bağlantı kontrol ediliyor.");
    public ConversationDraft? Draft { get; set; }
}

public sealed record ConversationDraft(Guid Id, string SourceUrl, string Question);
