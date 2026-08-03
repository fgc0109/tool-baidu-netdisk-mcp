namespace BaiduNetdisk.Management;

public sealed record BaiduBatchOperationResult(
    string Operation,
    IReadOnlyList<BaiduFileOperationResult> Items,
    string? RequestId)
{
    public bool Success => Items.All(item => item.Success);
}
