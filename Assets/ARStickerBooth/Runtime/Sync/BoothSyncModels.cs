using System;

namespace PhotoBooth.Booth.Sync
{
    [Serializable]
    public sealed class BoothBackendScaffoldConfig
    {
        public string DeviceId;
        public string DeviceToken;
        public string BoothApiBaseUrl;
        public string PublishApiBaseUrl;
        public string DownloadBaseUrl;
        public string AssetUploadPathTemplate;
        public string AssetRegistrationPathTemplate;
        public string PublishPathTemplate;
        public bool SeparateAssetRegistration;
        public bool UploadThumbnail;
        public int RequestTimeoutSeconds;
    }

    [Serializable]
    public sealed class SyncJobRequest
    {
        public string JobId;
        public string DeviceId;
        public string ThemeId;
        public string ComposedImagePath;
        public string ThumbnailPath;
        public string CurrencyCode;
        public long AmountMinorUnits;
        public string PaymentReference;
    }

    [Serializable]
    public sealed class SyncJobResult
    {
        public bool Success;
        public bool Retryable;
        public string Message;
        public string RemoteAssetKey;
        public string DownloadUrl;
    }

    [Serializable]
    public sealed class BoothApiError
    {
        public string code;
        public string message;
    }

    [Serializable]
    public sealed class BoothAssetRecord
    {
        public string asset_type;
        public string remote_key;
        public string content_type;
        public string checksum;
    }

    [Serializable]
    public sealed class BoothAssetUploadResponseData
    {
        public string remote_asset_key;
        public string remote_key;
        public string download_url;
        public BoothAssetRecord[] assets;
    }

    [Serializable]
    public sealed class BoothAssetUploadResponseEnvelope
    {
        public bool success;
        public BoothAssetUploadResponseData data;
        public BoothApiError error;
    }

    [Serializable]
    public sealed class BoothAssetRegistrationRequest
    {
        public BoothAssetRegistrationItem[] assets;
    }

    [Serializable]
    public sealed class BoothAssetRegistrationItem
    {
        public string asset_type;
        public string remote_key;
        public string content_type;
        public string checksum;
    }

    [Serializable]
    public sealed class BoothRegistrationResponseData
    {
        public bool accepted;
        public string remote_asset_key;
        public string remote_key;
        public BoothAssetRecord[] assets;
    }

    [Serializable]
    public sealed class BoothRegistrationResponseEnvelope
    {
        public bool success;
        public BoothRegistrationResponseData data;
        public BoothApiError error;
    }

    [Serializable]
    public sealed class BoothPublishRequest
    {
        public string primary_asset_type;
    }

    [Serializable]
    public sealed class BoothPublishResponseData
    {
        public string job_id;
        public string upload_status;
        public string download_url;
        public string remote_asset_key;
        public string remote_key;
        public BoothAssetRecord[] assets;
    }

    [Serializable]
    public sealed class BoothPublishResponseEnvelope
    {
        public bool success;
        public BoothPublishResponseData data;
        public BoothApiError error;
    }
}
