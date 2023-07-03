﻿using AutoMapper;
using Files.Application.Common.Exceptions;
using Files.Application.Features.Directory.DataTransferObjects;
using Files.Application.Features.File.Services;
using Files.Infrastructure.Persistence;
using Files.Infrastructure.Persistence.RepositoryManagers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Files.Application.Features.Directory.Services;

public class DirectoryService : IDirectoryService
{
    private readonly ILogger<DirectoryService> _logger;
    private readonly IMapper _mapper;
    private readonly IRepositoryManager _repositoryManager;
    private readonly IFileService _fileService;

    public DirectoryService(
        ILogger<DirectoryService> logger,
        IMapper mapper,
        IRepositoryManager repositoryManager, 
        IFileService fileService)
    {
        _logger = logger;
        _mapper = mapper;
        _repositoryManager = repositoryManager;
        _fileService = fileService;
        _logger.LogInformation("Directory Service is called");
    }

    public async Task<DirectoryDto> GetDirectoryAsync(
        Guid userId, Guid directoryId, CancellationToken cancellationToken = default)
    {
        var directory = await FindDirectoryWithChildElementsAsync(userId, directoryId, cancellationToken);
        var dto = _mapper.Map<DirectoryDto>(directory);
        return dto;
    }
    public async Task<ICollection<DirectoryDto>> GetDirectoriesAsync(
        Guid userId, 
        bool trackChanges = false,
        CancellationToken cancellationToken = default)
    {
        var directories = await _repositoryManager.DirectoryRepository
            .FindAll(trackChanges)
            .Include(x => x.Files)
            .Include(x => x.Directories)
            .Where(x => x.UserId == userId)
            .ToListAsync(cancellationToken);
        
        return _mapper.Map<ICollection<DirectoryDto>>(directories);
    }
    public async Task<DirectoryDto> CreateDirectoryAsync(
        Guid userId, 
        CreateDirectoryDto dto,
        CancellationToken cancellationToken = default)
    {
        var directory = _mapper.Map<Domain.Entities.Directory.Directory>(dto);
        if (directory.ParentDirectoryId.HasValue)
        {
            var parent = await _repositoryManager.DirectoryRepository
                .FindSingleAsync(x => x.Id == dto.ParentDirectoryId, cancellationToken);

            if (parent is null)
            {
                throw new InvalidDirectoryIdBadRequestException(directory.ParentDirectoryId.Value);
            }

            directory.ParentDirectoryId = parent.Id;
        }
        directory.Created = DateTime.Now;;
        directory.UserId = userId;

        if (dto.Directories.Count > 0)
        {
            directory.Directories = new List<Domain.Entities.Directory.Directory>();
            foreach (var directoryId in dto.Directories)
            {
                var dir = await FindDirectoryAsync(userId, directoryId, cancellationToken);
                directory.Directories.Add(dir);
            }
        }

        if (dto.Files.Count > 0)
        {
            directory.Files = new List<Domain.Entities.File.File>();
            foreach (var fileId in dto.Files)
            {
                var file = await _repositoryManager.FileRepository
                    .FindSingleAsync(x => x.Id == fileId, cancellationToken);
                if (file is null)
                {
                    throw new InvalidFileIdBadRequestException(fileId);
                }
                directory.Files.Add(file);
            }
        }
        
        await _repositoryManager.DirectoryRepository.CreateAsync(directory, cancellationToken);
        await _repositoryManager.SaveChangesAsync(cancellationToken);
        
        _logger.LogInformation($"Directory with id: {directory.Id} created");
        return _mapper.Map<DirectoryDto>(directory);
    }

    public async Task<DirectoryDto> UpdateDirectoryAsync(
        Guid userId, 
        UpdateDirectoryDto dto,
        CancellationToken cancellationToken = default)
    {
        var directory = await FindDirectoryWithChildElementsAsync(userId, dto.Id, cancellationToken);

        _mapper.Map(dto, directory);
        
        if (directory.ParentDirectoryId.HasValue)
        {
            var parent = await _repositoryManager.DirectoryRepository
                .FindSingleAsync(x => x.Id == dto.ParentDirectoryId, cancellationToken);

            if (parent is null)
            {
                throw new InvalidDirectoryIdBadRequestException(directory.ParentDirectoryId.Value);
            }

            directory.ParentDirectoryId = parent.Id;
        }
        directory.Updated = DateTime.Now;;
        
        directory.Directories.Clear();
        foreach (var directoryId in dto.Directories)
        {
            var dir = await FindDirectoryAsync(userId, directoryId, cancellationToken);
            directory.Directories.Add(dir);
        }

        
        directory.Files.Clear();
        foreach (var fileId in dto.Files)
        {
            var file = await _repositoryManager.FileRepository
                .FindSingleAsync(x => x.Id == fileId, cancellationToken);
            if (file is null)
            {
                throw new InvalidFileIdBadRequestException(fileId);
            }
            directory.Files.Add(file);
        }
        
        _repositoryManager.DirectoryRepository.Update(directory);
        await _repositoryManager.SaveChangesAsync(cancellationToken);
        _logger.LogInformation($"Directory with id: {directory.Id} updated");

        return _mapper.Map<DirectoryDto>(directory);
    }
    public async Task DeleteDirectoryAsync(
        Guid userId, 
        Guid directoryId,
        CancellationToken cancellationToken = default)
    {
        var directory = await FindDirectoryWithNestedElementsAsync(userId, directoryId, cancellationToken);
        await RemoveNestedElements(directory, userId);
        _repositoryManager.DirectoryRepository.Remove(directory);
        await _repositoryManager.SaveChangesAsync(cancellationToken);
        _logger.LogInformation($"Directory with id: {directory.Id} deleted");
    }
    private async Task RemoveNestedElements(Domain.Entities.Directory.Directory directory, Guid userId)
    {
        await RemoveNestedFilesAsync(directory, userId);
        foreach (var child in directory.Directories)
        {
            await RemoveNestedFilesAsync(child, userId);
            await RemoveNestedElements(child, userId);
            _repositoryManager.DirectoryRepository.Remove(child);
        }
    }
    private async Task RemoveNestedFilesAsync(Domain.Entities.Directory.Directory directory, Guid userId)
    {
        if (directory.Files.Any())
        {
            foreach (var file in directory.Files)
            {
                await _fileService.DeleteFileAsync(userId, directory.Id, file.Id);
            }
        }
    }
    private async Task<Domain.Entities.Directory.Directory> FindDirectoryAsync(
        Guid userId, 
        Guid directoryId,
        CancellationToken cancellationToken = default)
    {
        var directory = await _repositoryManager.DirectoryRepository
            .FindSingleAsync(x => x.UserId == userId && x.Id == directoryId, cancellationToken);
        
        if (directory is null)
        {
            throw new InvalidDirectoryIdBadRequestException(directoryId);
        }

        return directory;
    }

    private async Task<Domain.Entities.Directory.Directory> FindDirectoryWithChildElementsAsync(
        Guid userId,
        Guid directoryId,
        CancellationToken cancellationToken = default)
    {
        var directory = await _repositoryManager
            .DirectoryRepository
            .FindByCondition(x => x.UserId == userId && x.Id == directoryId, false)
            .Include(x => x.Directories)
            .Include(x => x.Files)
            .FirstOrDefaultAsync(cancellationToken);
        
        if (directory is null)
        {
            throw new InvalidDirectoryIdBadRequestException(directoryId);
        }

        return directory;
    }
    
    private async Task<Domain.Entities.Directory.Directory> FindDirectoryWithNestedElementsAsync(
        Guid userId,
        Guid directoryId,
        CancellationToken cancellationToken = default)
    {
        var directories = await _repositoryManager
            .DirectoryRepository
            .FindAll(true)
            .Include(x => x.Directories)
            .Include(x => x.Files)
            .Where(x => x.UserId == userId)
            .ToListAsync(cancellationToken);

        var directory = directories.FirstOrDefault(x => x.Id == directoryId);
        
        if (directory is null)
        {
            throw new InvalidDirectoryIdBadRequestException(directoryId);
        }

        return directory;
    } 
}