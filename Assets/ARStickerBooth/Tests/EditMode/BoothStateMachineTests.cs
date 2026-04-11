using System;
using NUnit.Framework;
using PhotoBooth.Booth.Domain;
using PhotoBooth.Booth.Services;

namespace PhotoBooth.Booth.Tests.EditMode
{
    public sealed class BoothStateMachineTests
    {
        [Test]
        public void StandardFlow_TransitionsToLinkReady()
        {
            var stateMachine = new BoothStateMachine();
            var job = stateMachine.CreateJob("JOB-TEST-0001", BuildPaths(), 12000, "THB");

            stateMachine.SelectTheme(job, "kawaii_01");
            stateMachine.SetPaymentPending(job, "PAY-REF-1");
            stateMachine.ConfirmPayment(job, "PAY-REF-1");
            stateMachine.BeginCapture(job);
            stateMachine.MarkCaptured(job, 4);
            stateMachine.BeginComposing(job);
            stateMachine.MarkComposed(job, "final.png", "thumb.png");
            stateMachine.BeginPrinting(job);
            stateMachine.MarkPrinted(job);
            stateMachine.QueueUpload(job);
            stateMachine.BeginUpload(job);
            stateMachine.MarkUploaded(job);
            stateMachine.MarkLinkReady(job, "https://example.com/d/JOB-TEST-0001");

            Assert.That(job.Status, Is.EqualTo(BoothJobStatus.LinkReady));
            Assert.That(job.PaymentStatus, Is.EqualTo(BoothPaymentStatus.Confirmed));
            Assert.That(job.UploadStatus, Is.EqualTo(BoothUploadStatus.LinkReady));
            Assert.That(job.DownloadUrl, Is.EqualTo("https://example.com/d/JOB-TEST-0001"));
            Assert.That(job.RawCaptureCount, Is.EqualTo(4));
        }

        [Test]
        public void InvalidTransition_Throws()
        {
            var stateMachine = new BoothStateMachine();
            var job = stateMachine.CreateJob("JOB-TEST-0002", BuildPaths(), 12000, "THB");

            var exception = Assert.Throws<InvalidOperationException>(() => stateMachine.BeginPrinting(job));
            Assert.That(exception.Message, Does.Contain("Created -> Printing"));
        }

        private static BoothJobPaths BuildPaths()
        {
            return new BoothJobPaths
            {
                RootDirectory = "/tmp/job",
                RawDirectory = "/tmp/job/raw",
                ComposedDirectory = "/tmp/job/composed",
                ThumbsDirectory = "/tmp/job/thumbs",
                LogsDirectory = "/tmp/job/logs",
                SnapshotPath = "/tmp/job/job.json"
            };
        }
    }
}
