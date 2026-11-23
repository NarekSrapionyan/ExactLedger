namespace el.Infrastructure.Persistence;

internal sealed class BatchRow
{
    public Guid Id { get; set; }
    public DateOnly BusinessDate { get; set; }
    public string Currency { get; set; } = string.Empty;
    public DateTimeOffset CutAtUtc { get; set; }
    public long CutSequence { get; set; }
    public int EntryCount { get; set; }
    public string TotalMinor { get; set; } = string.Empty;
}

internal sealed class InstructionRow
{
    public Guid Id { get; set; }
    public string Reference { get; set; } = string.Empty;
    public Guid PartyId { get; set; }
    public long AmountMinor { get; set; }
}

internal sealed class ExportMetaRow
{
    public string Sha256 { get; set; } = string.Empty;
    public int RecordCount { get; set; }
    public string TotalMinor { get; set; } = string.Empty;
    public DateTimeOffset GeneratedAtUtc { get; set; }
}

internal sealed class ExportContentRow
{
    public byte[] Content { get; set; } = [];
    public string Sha256 { get; set; } = string.Empty;
    public int RecordCount { get; set; }
    public string TotalMinor { get; set; } = string.Empty;
    public DateTimeOffset GeneratedAtUtc { get; set; }
}

internal sealed class ConfirmationRow
{
    public string StatementSha256 { get; set; } = string.Empty;
    public int InstructionCount { get; set; }
    public string TotalMinor { get; set; } = string.Empty;
    public DateTimeOffset ConfirmedAtUtc { get; set; }
}

internal sealed class CandidateRow
{
    public Guid Id { get; set; }
    public long Sequence { get; set; }
    public string Kind { get; set; } = string.Empty;
}

internal sealed class PayableRow
{
    public Guid PartyId { get; set; }
    public string CreditTotal { get; set; } = string.Empty;
    public string DebitTotal { get; set; } = string.Empty;
    public string Reserved { get; set; } = string.Empty;
}

internal sealed class EntryFactRow
{
    public Guid Id { get; set; }
    public string Currency { get; set; } = string.Empty;
    public bool Finalized { get; set; }
    public int LineCount { get; set; }
    public string DebitTotal { get; set; } = string.Empty;
    public string CreditTotal { get; set; } = string.Empty;
}
