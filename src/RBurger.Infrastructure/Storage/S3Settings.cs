namespace RBurger.Infrastructure.Storage;

public class S3Settings
{
    public const string SectionName = "AWS";

    public string Region { get; set; } = string.Empty;
    public string BucketName { get; set; } = string.Empty;
}