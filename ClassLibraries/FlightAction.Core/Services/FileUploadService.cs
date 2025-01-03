﻿using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using CSharpFunctionalExtensions;
using FlightAction.Core.Api;
using FlightAction.Core.Services.Interfaces;
using FlightAction.DTO;
using FlightAction.DTO.Enum;
using Flurl.Http;
using Flurl.Http.Content;
using Framework.Base.ModelEntity;
using Framework.Extensions;
using Framework.Models;
using Framework.Utility;
using Framework.Utility.Interfaces;
using Microsoft.Extensions.Configuration;
using Serilog;

namespace FlightAction.Core.Services
{
    public class FileUploadService : IFileUploadService
    {
        private readonly IDirectoryUtility _directoryUtility;
        private readonly ILogger _logger;
        private readonly string _baseUrl;
        private readonly string _userName;
        private readonly string _password;
        private readonly string _processedDirectory;
        private readonly string _FailureDirectory;
        private readonly string _apiKey;
        private readonly FileLocation _fileLocation;

        private static string _token;

        private const string ProcessedFileLocation = "Processed";
        private const string NoNewFileToUploadMessage = "No new files available to upload";

        public FileUploadService(IConfiguration configuration, IDirectoryUtility directoryUtility, ILogger logger)
        {
            _baseUrl = System.Configuration.ConfigurationSettings.AppSettings["ServerHost"];
            _userName = System.Configuration.ConfigurationSettings.AppSettings["UserId"];
            _password = System.Configuration.ConfigurationSettings.AppSettings["Password"];
            _processedDirectory = System.Configuration.ConfigurationSettings.AppSettings["ProcessedDirectory"];
            _FailureDirectory = System.Configuration.ConfigurationSettings.AppSettings["FailureDirectory"];
            _fileLocation = new FileLocation
            {
                Air = System.Configuration.ConfigurationSettings.AppSettings["FileLocation:Air"],
                Pnr = System.Configuration.ConfigurationSettings.AppSettings["FileLocation:Pnr"],
                Mir = System.Configuration.ConfigurationSettings.AppSettings["FileLocation:Mir"]
            };

            _directoryUtility = directoryUtility;
            _logger = logger;
        }

        public async Task ProcessFilesAsync()
        {
            await TryCatchExtension.ExecuteAndHandleErrorAsync(
                async () =>
                {
                    foreach (var prop in _fileLocation.GetType().GetProperties())
                    {
                        var currentDirectory = prop.GetValue(_fileLocation, null).ToString();

                        if (!Directory.Exists(currentDirectory))
                        {
                            Directory.CreateDirectory(currentDirectory);
                        }

                        var getResult = _directoryUtility.GetAllFilesInDirectory(currentDirectory);
                        if (getResult.HasNoValue)
                        {
                            _logger.Information(NoNewFileToUploadMessage);
                            continue;
                        }

                        _logger.Information($"Started processing [{getResult.Value.Count()}] file(s) from location: [{currentDirectory}]. Start-Time: {DateTime.UtcNow}");

                        //var currentProcessedDirectory = PrepareProcessedDirectory(currentDirectory, prop.Name);
                        
                        

                        foreach (var filePath in getResult.Value)
                        {
                            var fileUploadResult = await UploadFileToServerAsync(filePath);
                            if (fileUploadResult.IsSuccess)
                            {
                                var currentProcessedDirectory = PrepareProcessedDirectoryByFileType(prop.Name);
                                _directoryUtility.Move(filePath, currentProcessedDirectory);
                            }
                            else {

                                // move the failed file to failure directory
                                var currentFailureFileDirectory = PrepareFailureFileDirectory(prop.Name);
                                _directoryUtility.Move(filePath, currentFailureFileDirectory);

                            }
                        }

                        _logger.Information($"Processing files from location: [{currentDirectory}] completed at: {DateTime.UtcNow}");
                    }
                },
                ex =>
                {
                    _logger.Fatal(ex, $"Error occurred in {nameof(ProcessFilesAsync)}. Exception Message:{ex.Message}. Details: {ex.GetExceptionDetailMessage()}");
                    return false;
                });
        }

        private string PrepareProcessedDirectory(string currentDirectory, string fileType)
        {
            var currentProcessedDirectory = Path.Combine(currentDirectory.Replace(fileType, ""), @"AutoCountCaptured");
            currentProcessedDirectory = Path.Combine(currentProcessedDirectory, fileType, DateTime.Now.ToString(Constants.DateFormatter.yyyy_MM_dd_Dash_Delimited));

            _directoryUtility.CreateFolderIfNotExistAsync(currentProcessedDirectory);

            return currentProcessedDirectory;
        }

        private string PrepareProcessedDirectoryByFileType(string fileType)
        {
            var currentProcessedDirectory = Path.Combine(_processedDirectory, fileType);
            currentProcessedDirectory = Path.Combine(currentProcessedDirectory, DateTime.Now.Year.ToString() + '-' + DateTime.Now.Month.ToString(), DateTime.Now.ToString(Constants.DateFormatter.yyyy_MM_dd_Dash_Delimited));

            _directoryUtility.CreateFolderIfNotExistAsync(currentProcessedDirectory);

            return currentProcessedDirectory;
        }
        private string PrepareFailureFileDirectory(string fileType)
        {
            var currentProcessedDirectory = Path.Combine(_FailureDirectory, fileType);
            currentProcessedDirectory = Path.Combine(currentProcessedDirectory, DateTime.Now.Year.ToString() + '-' + DateTime.Now.Month.ToString(), DateTime.Now.ToString(Constants.DateFormatter.yyyy_MM_dd_Dash_Delimited));

            _directoryUtility.CreateFolderIfNotExistAsync(currentProcessedDirectory);

            return currentProcessedDirectory;
        }


        private async Task<Result<bool>> UploadFileToServerAsync(string filePath)
        {
            return await TryCatchExtension.ExecuteAndHandleErrorAsync(
                 async () =>
                 {
                     if (string.IsNullOrWhiteSpace(_token) || _token.IsExpired())
                     {
                         var jsonAuthContent = FlurlHttp.GlobalSettings.JsonSerializer.Serialize(new AuthenticateRequestDTO
                         {
                             UserName = _userName,
                             Password = _password
                         });

                         var authContent = new CapturedStringContent(jsonAuthContent, "application/json-patch+json");

                         var authenticateResponse = await _baseUrl
                             .WithHeader(ApiCollection.DefaultHeader, ApiCollection.FileUploadApi.DefaultVersion)
                             .AppendPathSegment(ApiCollection.AuthenticationApi.Segment)
                             .PostAsync(authContent)
                             .ReceiveJson<PrometheusResponse>();

                         var responseData = authenticateResponse.Data.ToString().DeserializeObject<AuthenticateResponseDTO>();
                         _token = responseData.Token;
                     }

                     var jsonFileUploadContent = FlurlHttp.GlobalSettings.JsonSerializer.Serialize(new TicketFileDTO
                     {
                         FileName = Path.GetFileName(filePath),
                         FileType = GetFileType(filePath),
                         MachineInfoDTO = MachineInfoDTO.Create(),
                         FileBytes = File.ReadAllBytes(filePath)
                     });

                     var fileUploadContent = new CapturedStringContent(jsonFileUploadContent, "application/json-patch+json");

                     var result = await _baseUrl
                              .WithHeaders(new
                              {
                                  Authorization = $"Bearer {_token}",
                                  Accept = "application/json",
                                  ProApiVersion = ApiCollection.FileUploadApi.DefaultVersion
                              })
                              .AppendPathSegment(ApiCollection.FileUploadApi.Segment)
                              .PostAsync(fileUploadContent).ReceiveJson<PrometheusResponse>();

                     return result.StatusCode == HttpStatusCode.OK ? Result.Success(true) : Result.Failure<bool>("File upload failed. Please check log");
                 },
                 ex => new TryCatchExtensionResult<Result<bool>>
                 {
                     AdditionalAction = () =>
                     {
                         _logger.Fatal(ex, $"Error occurred in {nameof(UploadFileToServerAsync)}. Exception Message:{ex.Message}. Details: {ex.GetExceptionDetailMessage()}");
                     },

                     DefaultResult = Result.Failure<bool>($"Error message: {ex.Message}. Details: {ex.GetExceptionDetailMessage()}"),
                     RethrowException = false
                 });
        }

        private FileTypeEnum GetFileType(string filePath)
        {
            var fileType = FileTypeEnum.Air;

            switch (filePath)
            {
                // ReSharper disable once AssignNullToNotNullAttribute
                case var _ when filePath.ToLower().Contains(Enum.GetName(typeof(FileTypeEnum), FileTypeEnum.Air)?.ToLower()):
                    fileType = FileTypeEnum.Air;
                    break;

                // ReSharper disable once AssignNullToNotNullAttribute
                case var _ when filePath.ToLower().Contains(Enum.GetName(typeof(FileTypeEnum), FileTypeEnum.Mir)?.ToLower()):
                    fileType = FileTypeEnum.Mir;
                    break;

                // ReSharper disable once AssignNullToNotNullAttribute
                case var _ when filePath.ToLower().Contains(Enum.GetName(typeof(FileTypeEnum), FileTypeEnum.Pnr)?.ToLower()):
                    fileType = FileTypeEnum.Pnr;
                    break;

            }

            return fileType;
        }
    }

}
