using System;
using System.IO;
using System.Security.Claims;
using System.Threading;
using Microsoft.AspNetCore.Mvc;
using Internal.Database;
using Internal.Shared;
using Microsoft.Extensions.Options;

[ApiController]
[Route("/api/users")]
public class UsersController : ControllerBase
{
    private readonly DatabaseHandler DBHandler;

    public UsersController(DatabaseHandler DBHandler_)
    {
        DBHandler = DBHandler_;
    }

    [HttpPost]
    public async Task<IActionResult> UploadAvatarImage(IFormFile file)
    {


        try
        {
            var AvatarFileUploadLimit = 8388608;
            var UserId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

            if (file == null || file.Length == 0) return BadRequest("No file.");
            if (file.Length > AvatarFileUploadLimit) return BadRequest("File too big!");
            if (string.IsNullOrWhiteSpace(UserId)) return BadRequest("Not logged in");

            var Extension = Path.GetExtension(file.FileName);

            if (!SharedMethods.AllowedExtension(Extension)) return BadRequest("This file extension isn't supported.");
            if (!SharedMethods.AllowedMime(file.ContentType)) return BadRequest("This mime type isn't supported.");

            var AvatarImagesPath = Path.Combine(Directory.GetCurrentDirectory(), "AvatarUploads");
            Directory.CreateDirectory(AvatarImagesPath);       
            var NewAvatarImageId = Guid.NewGuid(); 
            var NewAvatarImageName = $"{NewAvatarImageId}{Extension}";
            var FullPath = Path.Combine(AvatarImagesPath, NewAvatarImageName);
            var StoragePath = $"AvatarUploads/{NewAvatarImageName}";
            await using var stream = new FileStream(FullPath, FileMode.CreateNew);
            await file.CopyToAsync(stream);

            var Success = await DBHandler.ExecuteAsync(@"
                INSERT INTO avatar_uploads (
                    id,
                    user_id,
                    file_name,
                    file_size,
                    mime_type,
                    storage_path
                )
                VALUES (
                    @id,
                    @user_id,
                    @file_name,
                    @file_size,
                    @mime_type,
                    @storage_path
                );
            ", cmd => 
            {
                cmd.Parameters.AddWithValue("id", NewAvatarImageId);
                cmd.Parameters.AddWithValue("user_id", UserId);
                cmd.Parameters.AddWithValue("file_name", NewAvatarImageName);
                cmd.Parameters.AddWithValue("file_size", file.Length);
                cmd.Parameters.AddWithValue("mime_type", file.ContentType);                              
                cmd.Parameters.AddWithValue("storage_path", StoragePath);                              
            }).ContinueWith(r => r.Result > 0);

            if (!Success)
            {
                try
                {
                    if (System.IO.File.Exists(StoragePath))
                    {
                        System.IO.File.Delete(StoragePath);
                    }
                } catch (Exception error)
                {
                    return BadRequest("Error with uploading file please try again later.");
                }
            }

            return Ok(new
            {
                success = Success,
                avatarImageId = NewAvatarImageId
            });
        
        } catch (Exception error) 
        {
            Console.WriteLine(error);
            return StatusCode(StatusCodes.Status500InternalServerError, new {success = false});
        }
    }
}