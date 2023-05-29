using Microsoft.AspNetCore.Mvc;

namespace Prive.Server.Http.Controllers;

[ApiController]
[Route("fortnite")]
public class CloudStorageController : ControllerBase {
    [HttpGet("api/cloudstorage/system")]
    public object CloudStorageSystem() {
        throw new NotImplementedException();
    }
}

public static class CloudStorageFiles {
}

public class CloudStorageFile {
    public string Filename { get; set; }
    public string Hash { get; set; }
}