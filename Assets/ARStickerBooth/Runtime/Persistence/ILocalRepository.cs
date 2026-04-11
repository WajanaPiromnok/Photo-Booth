using System.Collections.Generic;
using PhotoBooth.Booth.Domain;

namespace PhotoBooth.Booth.Persistence
{
    public interface ILocalRepository
    {
        void Initialize();
        BoothJobPaths CreateJobPaths(string jobId);
        BoothJob SaveNew(BoothJob job);
        BoothJob Save(BoothJob job);
        BoothJob Get(string jobId);
        IReadOnlyList<BoothJob> List();
    }
}
