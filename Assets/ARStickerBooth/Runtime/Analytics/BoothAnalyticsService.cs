using System;
using PhotoBooth.Booth.Domain;
using PhotoBooth.Booth.Content;
using Unity.Services.Analytics;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PhotoBooth.Booth.Services
{
    public sealed class BoothAnalyticsService : IBoothTelemetrySink
    {
        private readonly string boothId;
        private readonly Func<string> contentVersionProvider;

        private bool isReady;
        private bool dataCollectionStarted;
        private string visitorSessionId;
        private float visitorSessionStartedAtRealtime;
        private string currentSceneId;
        private float currentSceneStartedAtRealtime;

        public BoothAnalyticsService(string boothId, Func<string> contentVersionProvider = null)
        {
            this.boothId = string.IsNullOrWhiteSpace(boothId) ? "booth-local" : boothId.Trim();
            this.contentVersionProvider = contentVersionProvider;
        }

        public bool IsReady => isReady;
        public bool HasActiveVisitorSession => !string.IsNullOrWhiteSpace(visitorSessionId);

        public void MarkReady(bool startDataCollection)
        {
            isReady = true;

            if (startDataCollection && !dataCollectionStarted)
            {
                AnalyticsService.Instance.StartDataCollection();
                dataCollectionStarted = true;
            }

            var activeScene = SceneManager.GetActiveScene();
            if (activeScene.IsValid() && !string.IsNullOrWhiteSpace(activeScene.name))
            {
                EnterScene(activeScene.name);
            }

            RecordEvent("booth_runtime_ready", analyticsEvent =>
            {
                analyticsEvent["ready_at_utc"] = DateTime.UtcNow.ToString("O");
            });
        }

        public void EnterScene(string sceneId)
        {
            if (string.IsNullOrWhiteSpace(sceneId))
            {
                return;
            }

            if (string.Equals(currentSceneId, sceneId, StringComparison.Ordinal))
            {
                return;
            }

            ExitScene();

            currentSceneId = sceneId.Trim();
            currentSceneStartedAtRealtime = Time.realtimeSinceStartup;

            RecordEvent("booth_scene_entered", analyticsEvent =>
            {
                analyticsEvent["scene_id"] = currentSceneId;
            });
        }

        public void ExitScene()
        {
            if (string.IsNullOrWhiteSpace(currentSceneId))
            {
                return;
            }

            var durationSeconds = Mathf.Max(0, Mathf.RoundToInt(Time.realtimeSinceStartup - currentSceneStartedAtRealtime));
            var sceneId = currentSceneId;

            RecordEvent("booth_scene_exited", analyticsEvent =>
            {
                analyticsEvent["scene_id"] = sceneId;
                analyticsEvent["duration_seconds"] = durationSeconds;
            });

            currentSceneId = null;
            currentSceneStartedAtRealtime = 0f;
        }

        public void TrackFeatureUsed(string featureName, string themeId = null, string sceneIdOverride = null)
        {
            if (string.IsNullOrWhiteSpace(featureName))
            {
                return;
            }

            RecordEvent("booth_feature_used", analyticsEvent =>
            {
                analyticsEvent["feature_name"] = featureName.Trim();
                AddStringParameter(analyticsEvent, "theme_id", themeId);
                AddStringParameter(analyticsEvent, "scene_id", sceneIdOverride);
            });
        }

        public void TrackDownloadRequested(string jobId, string themeId = null)
        {
            RecordEvent("booth_download_requested", analyticsEvent =>
            {
                AddStringParameter(analyticsEvent, "job_id", jobId);
                AddStringParameter(analyticsEvent, "theme_id", themeId);
            });
        }

        public void TrackDownloadConfirmed(string jobId, string themeId = null, int totalDownloads = -1)
        {
            RecordEvent("booth_download_confirmed", analyticsEvent =>
            {
                AddStringParameter(analyticsEvent, "job_id", jobId);
                AddStringParameter(analyticsEvent, "theme_id", themeId);
                if (totalDownloads >= 0)
                {
                    analyticsEvent["download_count"] = totalDownloads;
                }
            });
        }

        public void TrackContentCheck(BoothContentSnapshot snapshot, string requestOrigin)
        {
            if (snapshot == null)
            {
                return;
            }

            RecordEvent("booth_content_checked", analyticsEvent =>
            {
                AddStringParameter(analyticsEvent, "installed_version", snapshot.InstalledVersion);
                AddStringParameter(analyticsEvent, "remote_version", snapshot.RemoteVersion);
                analyticsEvent["has_update"] = snapshot.HasUpdateAvailable;
                analyticsEvent["force_update"] = snapshot.ForceUpdate;
                AddStringParameter(analyticsEvent, "request_origin", requestOrigin);
            });
        }

        public void TrackContentInstalled(BoothContentSnapshot snapshot, int downloadDurationSeconds)
        {
            if (snapshot == null)
            {
                return;
            }

            RecordEvent("booth_content_installed", analyticsEvent =>
            {
                AddStringParameter(analyticsEvent, "installed_version", snapshot.InstalledVersion);
                AddStringParameter(analyticsEvent, "remote_version", snapshot.RemoteVersion);
                analyticsEvent["download_duration_seconds"] = downloadDurationSeconds;
                analyticsEvent["has_update"] = snapshot.HasUpdateAvailable;
            });
        }

        public void Flush()
        {
            if (!isReady)
            {
                return;
            }

            AnalyticsService.Instance.Flush();
        }

        public void OnJobCreated(BoothJob job)
        {
            EnsureVisitorSession(job);

            RecordEvent("booth_job_created", analyticsEvent =>
            {
                AddJobParameters(analyticsEvent, job);
            });
        }

        public void OnThemeSelected(BoothJob job)
        {
            EnsureVisitorSession(job);

            RecordEvent("booth_theme_selected", analyticsEvent =>
            {
                AddJobParameters(analyticsEvent, job);
                AddStringParameter(analyticsEvent, "theme_id", job.ThemeId);
            });
        }

        public void OnPaymentPending(BoothJob job)
        {
            RecordEvent("booth_payment_pending", analyticsEvent =>
            {
                AddJobParameters(analyticsEvent, job);
            });
        }

        public void OnPaymentConfirmed(BoothJob job)
        {
            RecordEvent("booth_payment_confirmed", analyticsEvent =>
            {
                AddJobParameters(analyticsEvent, job);
            });
        }

        public void OnCaptureCompleted(BoothJob job)
        {
            RecordEvent("booth_capture_completed", analyticsEvent =>
            {
                AddJobParameters(analyticsEvent, job);
                analyticsEvent["raw_capture_count"] = job.RawCaptureCount;
            });
        }

        public void OnCompositionCompleted(BoothJob job)
        {
            RecordEvent("booth_composition_completed", analyticsEvent =>
            {
                AddJobParameters(analyticsEvent, job);
            });
        }

        public void OnPrintCompleted(BoothJob job)
        {
            RecordEvent("booth_print_completed", analyticsEvent =>
            {
                AddJobParameters(analyticsEvent, job);
                analyticsEvent["printed"] = true;
            });
        }

        public void OnDownloadLinkReady(BoothJob job)
        {
            RecordEvent("booth_download_link_ready", analyticsEvent =>
            {
                AddJobParameters(analyticsEvent, job);
                analyticsEvent["has_download_url"] = !string.IsNullOrWhiteSpace(job.DownloadUrl);
            });
        }

        public void OnJobCompleted(BoothJob job)
        {
            RecordEvent("booth_job_completed", analyticsEvent =>
            {
                AddJobParameters(analyticsEvent, job);
                analyticsEvent["printed"] = job.Status == BoothJobStatus.Printed || job.Status == BoothJobStatus.Done;
            });

            EndVisitorSession("done", job, null);
        }

        public void OnJobCancelled(BoothJob job, string reason)
        {
            RecordEvent("booth_job_cancelled", analyticsEvent =>
            {
                AddJobParameters(analyticsEvent, job);
                AddStringParameter(analyticsEvent, "reason", reason);
            });

            EndVisitorSession("cancelled", job, reason);
        }

        public void OnJobFailed(BoothJob job, string reason)
        {
            RecordEvent("booth_job_failed", analyticsEvent =>
            {
                AddJobParameters(analyticsEvent, job);
                AddStringParameter(analyticsEvent, "reason", reason);
            });

            EndVisitorSession("failed", job, reason);
        }

        private void EnsureVisitorSession(BoothJob job)
        {
            if (HasActiveVisitorSession)
            {
                return;
            }

            visitorSessionId = string.IsNullOrWhiteSpace(job?.JobId)
                ? Guid.NewGuid().ToString("N")
                : job.JobId;
            visitorSessionStartedAtRealtime = Time.realtimeSinceStartup;

            RecordEvent("booth_visitor_started", analyticsEvent =>
            {
                AddJobParameters(analyticsEvent, job);
            });
        }

        private void EndVisitorSession(string outcome, BoothJob job, string reason)
        {
            if (!HasActiveVisitorSession)
            {
                return;
            }

            var durationSeconds = Mathf.Max(0, Mathf.RoundToInt(Time.realtimeSinceStartup - visitorSessionStartedAtRealtime));

            RecordEvent("booth_visitor_completed", analyticsEvent =>
            {
                AddJobParameters(analyticsEvent, job);
                analyticsEvent["duration_seconds"] = durationSeconds;
                analyticsEvent["outcome"] = outcome;
                AddStringParameter(analyticsEvent, "reason", reason);
            });

            visitorSessionId = null;
            visitorSessionStartedAtRealtime = 0f;
        }

        private void RecordEvent(string eventName, Action<CustomEvent> populate)
        {
            if (!isReady)
            {
                return;
            }

            try
            {
                var analyticsEvent = new CustomEvent(eventName);
                analyticsEvent["booth_id"] = boothId;
                analyticsEvent["platform"] = Application.platform.ToString();
                analyticsEvent["app_version"] = string.IsNullOrWhiteSpace(Application.version) ? "unknown" : Application.version;

                if (HasActiveVisitorSession)
                {
                    analyticsEvent["visitor_session_id"] = visitorSessionId;
                }

                AddStringParameter(analyticsEvent, "current_scene_id", currentSceneId);

                var installedContentVersion = contentVersionProvider?.Invoke();
                AddStringParameter(analyticsEvent, "installed_content_version", installedContentVersion);

                populate?.Invoke(analyticsEvent);
                AnalyticsService.Instance.RecordEvent(analyticsEvent);
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"Analytics event '{eventName}' failed: {exception.Message}");
            }
        }

        private static void AddJobParameters(CustomEvent analyticsEvent, BoothJob job)
        {
            if (analyticsEvent == null || job == null)
            {
                return;
            }

            AddStringParameter(analyticsEvent, "job_id", job.JobId);
            AddStringParameter(analyticsEvent, "theme_id", job.ThemeId);
            AddStringParameter(analyticsEvent, "currency_code", job.CurrencyCode);
            AddStringParameter(analyticsEvent, "download_url", job.DownloadUrl);
            analyticsEvent["amount_minor_units"] = job.AmountMinorUnits;
            analyticsEvent["status"] = job.Status.ToString();
            analyticsEvent["payment_status"] = job.PaymentStatus.ToString();
            analyticsEvent["upload_status"] = job.UploadStatus.ToString();
        }

        private static void AddStringParameter(CustomEvent analyticsEvent, string key, string value)
        {
            if (analyticsEvent == null || string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            analyticsEvent[key] = value.Trim();
        }
    }
}
