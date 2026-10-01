using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Options;
using RBurger.Application.Common.Interfaces;

namespace RBurger.Infrastructure.Storage;

// §7.6.1/§10.1: uploads/deletes menu-item photos in S3, public-read (bucket policy handles
// this, not the object ACL - AWS blocks per-object ACLs by default on new buckets).
public class S3MenuItemImageStorage : IMenuItemImageStorage
{
    private readonly IAmazonS3 _s3Client;
    private readonly S3Settings _settings;

    public S3MenuItemImageStorage(IAmazonS3 s3Client, IOptions<S3Settings> settings)
    {
        _s3Client = s3Client;
        _settings = settings.Value;
    }

    public async Task<MenuItemImageUploadResult> UploadAsync(
        int menuItemId,
        Stream content,
        string contentType,
        string? existingObjectKeyToReplace,
        CancellationToken cancellationToken)
    {
        var extension = contentType switch
        {
            "image/jpeg" => "jpg",
            "image/png" => "png",
            "image/webp" => "webp",
            _ => "bin"
        };

        // §6.2: "menu-items/{id}/{uuid}.jpg"
        var objectKey = $"menu-items/{menuItemId}/{Guid.NewGuid()}.{extension}";

        var putRequest = new PutObjectRequest
        {
            BucketName = _settings.BucketName,
            Key = objectKey,
            InputStream = content,
            ContentType = contentType,
            AutoCloseStream = false
        };

        await _s3Client.PutObjectAsync(putRequest, cancellationToken);

        // §7.6.1: "deletes the previous object if one existed" - only after the new one
        // is written successfully.
        if (!string.IsNullOrWhiteSpace(existingObjectKeyToReplace))
        {
            await DeleteAsync(existingObjectKeyToReplace, cancellationToken);
        }

        var imageUrl =
            $"https://{_settings.BucketName}.s3.{_settings.Region}.amazonaws.com/{objectKey}";

        return new MenuItemImageUploadResult(imageUrl, objectKey);
    }

    public async Task DeleteAsync(string objectKey, CancellationToken cancellationToken)
    {
        await _s3Client.DeleteObjectAsync(_settings.BucketName, objectKey, cancellationToken);
    }
}