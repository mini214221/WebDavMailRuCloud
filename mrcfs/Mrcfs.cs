﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Threading;
using Fsp;
using Fsp.Interop;
using FileInfo = Fsp.Interop.FileInfo;
using Path = YaR.MailRuCloud.Fs.Path;

namespace YaR.MailRuCloud.Fs
{
    class Mrcfs : FileSystemBase
    {
        public const UInt16 MRCFS_SECTOR_SIZE = 512;
        public const UInt16 MRCFS_SECTORS_PER_ALLOCATION_UNIT = 1;

        private readonly Api.MailRuCloud _cloud;

        public Mrcfs(Boolean caseInsensitive, UInt32 maxFileNodes, UInt32 maxFileSize, String rootSddl, string login, string password)
        {
            _cloud = new Api.MailRuCloud(login, password, null);

            _fileNodeMap = new FileNodeMap(caseInsensitive);
            _maxFileNodes = maxFileNodes;
            _maxFileSize = maxFileSize;

            /*
             * Create root directory.
             */

            FileNode rootNode = new FileNode(_cloud, "\\")
            {
                FileInfo = {FileAttributes = (UInt32) FileAttributes.Directory}
            };
            if (null == rootSddl)
                rootSddl = "O:BAG:BAD:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;FA;;;WD)";
            RawSecurityDescriptor rootSecurityDescriptor = new RawSecurityDescriptor(rootSddl);
            rootNode.FileSecurity = new Byte[rootSecurityDescriptor.BinaryLength];
            rootSecurityDescriptor.GetBinaryForm(rootNode.FileSecurity, 0);

            _fileNodeMap.Insert(rootNode);
        }

        public override Int32 Init(Object host0)
        {
            FileSystemHost host = (FileSystemHost)host0;
            host.SectorSize = MRCFS_SECTOR_SIZE;
            host.SectorsPerAllocationUnit = MRCFS_SECTORS_PER_ALLOCATION_UNIT;
            host.VolumeCreationTime = (UInt64)DateTime.Now.ToFileTimeUtc();
            host.VolumeSerialNumber = (UInt32)(host.VolumeCreationTime / (10000 * 1000));
            host.CaseSensitiveSearch = !_fileNodeMap.CaseInsensitive;
            host.CasePreservedNames = true;
            host.UnicodeOnDisk = true;
            host.PersistentAcls = true;
            host.ReparsePoints = true;
            host.ReparsePointsAccessCheck = false;
            host.NamedStreams = true;
            host.PostCleanupWhenModifiedOnly = true;
            host.PassQueryDirectoryFileName = true;
            return STATUS_SUCCESS;
        }

        public override Int32 GetVolumeInfo(out VolumeInfo volumeInfo)
        {
            volumeInfo = default(VolumeInfo);
            volumeInfo.TotalSize = _maxFileNodes * (UInt64)_maxFileSize;
            volumeInfo.FreeSize = (_maxFileNodes - _fileNodeMap.Count()) * (UInt64)_maxFileSize;
            volumeInfo.SetVolumeLabel(_volumeLabel);
            return STATUS_SUCCESS;
        }

        public override Int32 SetVolumeLabel(String volumeLabel, out VolumeInfo volumeInfo)
        {
            _volumeLabel = volumeLabel;
            return GetVolumeInfo(out volumeInfo);
        }

        public override Int32 GetSecurityByName(
            String fileName,
            out UInt32 fileAttributes/* or ReparsePointIndex */,
            ref Byte[] securityDescriptor)
        {
            FileNode fileNode = _fileNodeMap.Get(fileName);
            if (null == fileNode)
            {
                Int32 result = STATUS_OBJECT_NAME_NOT_FOUND;
                if (FindReparsePoint(fileName, out fileAttributes))
                    result = STATUS_REPARSE;
                else
                    _fileNodeMap.GetParent(fileName, ref result);
                return result;
            }

            UInt32 fileAttributesMask = ~(UInt32)0;
            if (null != fileNode.MainFileNode)
            {
                fileAttributesMask = ~(UInt32)FileAttributes.Directory;
                fileNode = fileNode.MainFileNode;
            }
            fileAttributes = fileNode.FileInfo.FileAttributes & fileAttributesMask;
            if (null != securityDescriptor)
                securityDescriptor = fileNode.FileSecurity;

            return STATUS_SUCCESS;
        }

        public override Int32 Create(
            String fileName,
            UInt32 createOptions,
            UInt32 grantedAccess,
            UInt32 fileAttributes,
            Byte[] securityDescriptor,
            UInt64 allocationSize,
            out Object fileNode0,
            out Object fileDesc,
            out FileInfo fileInfo,
            out String normalizedName)
        {
            fileNode0 = default(Object);
            fileDesc = default(Object);
            fileInfo = default(FileInfo);
            normalizedName = default(String);

            Int32 result = STATUS_SUCCESS;

            var fileNode = _fileNodeMap.Get(fileName);
            if (null != fileNode)
                return STATUS_OBJECT_NAME_COLLISION;
            var parentNode = _fileNodeMap.GetParent(fileName, ref result);
            if (null == parentNode)
                return result;

            if (0 != (createOptions & FILE_DIRECTORY_FILE))
                allocationSize = 0;
            if (_fileNodeMap.Count() >= _maxFileNodes)
                return STATUS_CANNOT_MAKE;
            if (allocationSize > _maxFileSize)
                return STATUS_DISK_FULL;

            if ("\\" != parentNode.FileName)
                /* normalize name */
                fileName = parentNode.FileName + "\\" + Path.GetFileName(fileName);
            fileNode = new FileNode(_cloud, fileName)
            {
                MainFileNode = _fileNodeMap.GetMain(fileName),
                FileInfo =
                {
                    FileAttributes = 0 != (fileAttributes & (UInt32) FileAttributes.Directory)
                        ? fileAttributes
                        : fileAttributes | (UInt32) FileAttributes.Archive
                },
                FileSecurity = securityDescriptor
            };
            if (0 != allocationSize)
            {
                result = SetFileSizeInternal(fileNode, allocationSize, true);
                if (0 > result)
                    return result;
            }
            _fileNodeMap.Insert(fileNode);

            Interlocked.Increment(ref fileNode.OpenCount);
            fileNode0 = fileNode;
            fileInfo = fileNode.GetFileInfo();
            normalizedName = fileNode.FileName;

            return STATUS_SUCCESS;
        }

        public override Int32 Open(
            String fileName,
            UInt32 createOptions,
            UInt32 grantedAccess,
            out Object fileNode0,
            out Object fileDesc,
            out FileInfo fileInfo,
            out String normalizedName)
        {
            fileNode0 = default(Object);
            fileDesc = default(Object);
            fileInfo = default(FileInfo);
            normalizedName = default(String);

            var fileNode = _fileNodeMap.Get(fileName);
            if (null == fileNode)
            {
                var result = STATUS_OBJECT_NAME_NOT_FOUND;
                _fileNodeMap.GetParent(fileName, ref result);
                return result;
            }

            Interlocked.Increment(ref fileNode.OpenCount);
            fileNode0 = fileNode;
            fileInfo = fileNode.GetFileInfo();
            normalizedName = fileNode.FileName;

            return STATUS_SUCCESS;
        }

        public override Int32 Overwrite(
            Object fileNode0,
            Object fileDesc,
            UInt32 fileAttributes,
            Boolean replaceFileAttributes,
            UInt64 allocationSize,
            out FileInfo fileInfo)
        {
            fileInfo = default(FileInfo);

            FileNode fileNode = (FileNode)fileNode0;

            List<String> streamFileNames = new List<String>(_fileNodeMap.GetStreamFileNames(fileNode));
            foreach (String streamFileName in streamFileNames)
            {
                FileNode streamNode = _fileNodeMap.Get(streamFileName);
                if (0 == streamNode?.OpenCount)
                    _fileNodeMap.Remove(streamNode);
            }

            var result = SetFileSizeInternal(fileNode, allocationSize, true);
            if (0 > result)
                return result;
            if (replaceFileAttributes)
                fileNode.FileInfo.FileAttributes = fileAttributes | (UInt32)FileAttributes.Archive;
            else
                fileNode.FileInfo.FileAttributes |= fileAttributes | (UInt32)FileAttributes.Archive;
            fileNode.FileInfo.FileSize = 0;
            fileNode.FileInfo.LastAccessTime =
                fileNode.FileInfo.LastWriteTime =
                    fileNode.FileInfo.ChangeTime = (UInt64)DateTime.Now.ToFileTimeUtc();

            fileInfo = fileNode.GetFileInfo();

            return STATUS_SUCCESS;
        }

        public override void Cleanup(
            Object fileNode0,
            Object fileDesc,
            String fileName,
            UInt32 flags)
        {
            FileNode fileNode = (FileNode)fileNode0;
            FileNode mainFileNode = fileNode.MainFileNode ?? fileNode;

            if (0 != (flags & CleanupSetArchiveBit))
            {
                if (0 == (mainFileNode.FileInfo.FileAttributes & (UInt32)FileAttributes.Directory))
                    mainFileNode.FileInfo.FileAttributes |= (UInt32)FileAttributes.Archive;
            }

            if (0 != (flags & (CleanupSetLastAccessTime | CleanupSetLastWriteTime | CleanupSetChangeTime)))
            {
                UInt64 systemTime = (UInt64)DateTime.Now.ToFileTimeUtc();

                if (0 != (flags & CleanupSetLastAccessTime))
                    mainFileNode.FileInfo.LastAccessTime = systemTime;
                if (0 != (flags & CleanupSetLastWriteTime))
                    mainFileNode.FileInfo.LastWriteTime = systemTime;
                if (0 != (flags & CleanupSetChangeTime))
                    mainFileNode.FileInfo.ChangeTime = systemTime;
            }

            if (0 != (flags & CleanupSetAllocationSize))
            {
                const ulong allocationUnit = MRCFS_SECTOR_SIZE * MRCFS_SECTORS_PER_ALLOCATION_UNIT;
                UInt64 allocationSize = (fileNode.FileInfo.FileSize + allocationUnit - 1) /
                                        allocationUnit * allocationUnit;
                SetFileSizeInternal(fileNode, allocationSize, true);
            }

            if (0 != (flags & CleanupDelete) && !_fileNodeMap.HasChild(fileNode))
            {
                List<String> streamFileNames = new List<String>(_fileNodeMap.GetStreamFileNames(fileNode));
                foreach (String streamFileName in streamFileNames)
                {
                    FileNode streamNode = _fileNodeMap.Get(streamFileName);
                    if (null == streamNode)
                        continue; /* should not happen */
                    _fileNodeMap.Remove(streamNode);
                }
                _fileNodeMap.Remove(fileNode);
            }
        }

        public override void Close(
            Object fileNode0,
            Object fileDesc)
        {
            FileNode fileNode = (FileNode)fileNode0;
            Interlocked.Decrement(ref fileNode.OpenCount);
        }

        public override Int32 Read(
            Object fileNode0,
            Object fileDesc,
            IntPtr buffer,
            UInt64 offset,
            UInt32 length,
            out UInt32 bytesTransferred)
        {
            FileNode fileNode = (FileNode)fileNode0;

            if (offset >= fileNode.FileInfo.FileSize)
            {
                bytesTransferred = default(UInt32);
                return STATUS_END_OF_FILE;
            }

            var endOffset = offset + length;
            if (endOffset > fileNode.FileInfo.FileSize)
                endOffset = fileNode.FileInfo.FileSize;

            bytesTransferred = (UInt32)(endOffset - offset);
            Marshal.Copy(fileNode.FileData, (int)offset, buffer, (int)bytesTransferred);

            return STATUS_SUCCESS;
        }

        public override Int32 Write(
            Object fileNode0,
            Object fileDesc,
            IntPtr buffer,
            UInt64 offset,
            UInt32 length,
            Boolean writeToEndOfFile,
            Boolean constrainedIo,
            out UInt32 bytesTransferred,
            out FileInfo fileInfo)
        {
            FileNode fileNode = (FileNode)fileNode0;
            UInt64 endOffset;

            if (constrainedIo)
            {
                if (offset >= fileNode.FileInfo.FileSize)
                {
                    bytesTransferred = default(UInt32);
                    fileInfo = default(FileInfo);
                    return STATUS_SUCCESS;
                }
                endOffset = offset + length;
                if (endOffset > fileNode.FileInfo.FileSize)
                    endOffset = fileNode.FileInfo.FileSize;
            }
            else
            {
                if (writeToEndOfFile)
                    offset = fileNode.FileInfo.FileSize;
                endOffset = offset + length;
                if (endOffset > fileNode.FileInfo.FileSize)
                {
                    Int32 result = SetFileSizeInternal(fileNode, endOffset, false);
                    if (0 > result)
                    {
                        bytesTransferred = default(UInt32);
                        fileInfo = default(FileInfo);
                        return result;
                    }
                }
            }

            bytesTransferred = (UInt32)(endOffset - offset);
            Marshal.Copy(buffer, fileNode.FileData, (int)offset, (int)bytesTransferred);

            fileInfo = fileNode.GetFileInfo();

            return STATUS_SUCCESS;
        }

        public override Int32 Flush(
            Object fileNode0,
            Object fileDesc,
            out FileInfo fileInfo)
        {
            FileNode fileNode = (FileNode)fileNode0;

            /*  nothing to flush, since we do not cache anything */
            fileInfo = fileNode?.GetFileInfo() ?? default(FileInfo);

            return STATUS_SUCCESS;
        }

        public override Int32 GetFileInfo(
            Object fileNode0,
            Object fileDesc,
            out FileInfo fileInfo)
        {
            FileNode fileNode = (FileNode)fileNode0;

            fileInfo = fileNode.GetFileInfo();

            return STATUS_SUCCESS;
        }

        public override Int32 SetBasicInfo(
            Object fileNode0,
            Object fileDesc,
            UInt32 fileAttributes,
            UInt64 creationTime,
            UInt64 lastAccessTime,
            UInt64 lastWriteTime,
            UInt64 changeTime,
            out FileInfo fileInfo)
        {
            FileNode fileNode = (FileNode)fileNode0;

            if (null != fileNode.MainFileNode)
                fileNode = fileNode.MainFileNode;

            if (unchecked((UInt32)(-1)) != fileAttributes)
                fileNode.FileInfo.FileAttributes = fileAttributes;
            if (0 != creationTime)
                fileNode.FileInfo.CreationTime = creationTime;
            if (0 != lastAccessTime)
                fileNode.FileInfo.LastAccessTime = lastAccessTime;
            if (0 != lastWriteTime)
                fileNode.FileInfo.LastWriteTime = lastWriteTime;
            if (0 != changeTime)
                fileNode.FileInfo.ChangeTime = changeTime;

            fileInfo = fileNode.GetFileInfo();

            return STATUS_SUCCESS;
        }

        public override Int32 SetFileSize(
            Object fileNode0,
            Object fileDesc,
            UInt64 newSize,
            Boolean setAllocationSize,
            out FileInfo fileInfo)
        {
            FileNode fileNode = (FileNode)fileNode0;

            var result = SetFileSizeInternal(fileNode, newSize, setAllocationSize);
            fileInfo = 0 <= result ? fileNode.GetFileInfo() : default(FileInfo);

            return STATUS_SUCCESS;
        }

        private Int32 SetFileSizeInternal(
            FileNode fileNode,
            UInt64 newSize,
            Boolean setAllocationSize)
        {
            if (setAllocationSize)
            {
                if (fileNode.FileInfo.AllocationSize != newSize)
                {
                    if (newSize > _maxFileSize)
                        return STATUS_DISK_FULL;

                    byte[] fileData = null;
                    if (0 != newSize)
                        try
                        {
                            fileData = new byte[newSize];
                        }
                        catch
                        {
                            return STATUS_INSUFFICIENT_RESOURCES;
                        }
                    int copyLength = (int)Math.Min(fileNode.FileInfo.AllocationSize, newSize);
                    if (0 != copyLength)
                        Array.Copy(fileNode.FileData, fileData ?? throw new InvalidOperationException(), copyLength);

                    fileNode.FileData = fileData;
                    fileNode.FileInfo.AllocationSize = newSize;
                    if (fileNode.FileInfo.FileSize > newSize)
                        fileNode.FileInfo.FileSize = newSize;
                }
            }
            else
            {
                if (fileNode.FileInfo.FileSize != newSize)
                {
                    if (fileNode.FileInfo.AllocationSize < newSize)
                    {
                        const ulong allocationUnit = MRCFS_SECTOR_SIZE * MRCFS_SECTORS_PER_ALLOCATION_UNIT;
                        UInt64 allocationSize = (newSize + allocationUnit - 1) / allocationUnit * allocationUnit;
                        Int32 result = SetFileSizeInternal(fileNode, allocationSize, true);
                        if (0 > result)
                            return result;
                    }

                    if (fileNode.FileInfo.FileSize < newSize)
                    {
                        int copyLength = (int)(newSize - fileNode.FileInfo.FileSize);
                        if (0 != copyLength)
                            Array.Clear(fileNode.FileData, (int)fileNode.FileInfo.FileSize, copyLength);
                    }
                    fileNode.FileInfo.FileSize = newSize;
                }
            }

            return STATUS_SUCCESS;
        }

        public override Int32 CanDelete(
            Object fileNode0,
            Object fileDesc,
            String fileName)
        {
            FileNode fileNode = (FileNode)fileNode0;

            if (_fileNodeMap.HasChild(fileNode))
                return STATUS_DIRECTORY_NOT_EMPTY;

            return STATUS_SUCCESS;
        }

        public override Int32 Rename(
            Object fileNode0,
            Object fileDesc,
            String fileName,
            String newFileName,
            Boolean replaceIfExists)
        {
            FileNode fileNode = (FileNode)fileNode0;

            var newFileNode = _fileNodeMap.Get(newFileName);
            if (null != newFileNode && fileNode != newFileNode)
            {
                if (!replaceIfExists)
                    return STATUS_OBJECT_NAME_COLLISION;
                if (0 != (newFileNode.FileInfo.FileAttributes & (UInt32)FileAttributes.Directory))
                    return STATUS_ACCESS_DENIED;
            }

            if (null != newFileNode && fileNode != newFileNode)
                _fileNodeMap.Remove(newFileNode);

            List<String> descendantFileNames = new List<String>(_fileNodeMap.GetDescendantFileNames(fileNode));
            foreach (String descendantFileName in descendantFileNames)
            {
                FileNode descendantFileNode = _fileNodeMap.Get(descendantFileName);
                if (null == descendantFileNode)
                    continue; /* should not happen */
                _fileNodeMap.Remove(descendantFileNode);
                descendantFileNode.FileName =
                    newFileName + descendantFileNode.FileName.Substring(fileName.Length);
                _fileNodeMap.Insert(descendantFileNode);
            }

            return STATUS_SUCCESS;
        }

        public override Int32 GetSecurity(
            Object fileNode0,
            Object fileDesc,
            ref Byte[] securityDescriptor)
        {
            if (securityDescriptor == null) throw new ArgumentNullException(nameof(securityDescriptor));

            FileNode fileNode = (FileNode)fileNode0;

            if (null != fileNode.MainFileNode)
                fileNode = fileNode.MainFileNode;

            securityDescriptor = fileNode.FileSecurity;

            return STATUS_SUCCESS;
        }

        public override Int32 SetSecurity(
            Object fileNode0,
            Object fileDesc,
            AccessControlSections sections,
            Byte[] securityDescriptor)
        {
            FileNode fileNode = (FileNode)fileNode0;

            if (null != fileNode.MainFileNode)
                fileNode = fileNode.MainFileNode;

            fileNode.FileSecurity = ModifySecurityDescriptor(
                fileNode.FileSecurity, sections, securityDescriptor);

            return STATUS_SUCCESS;
        }

        public override Boolean ReadDirectoryEntry(
            Object fileNode0,
            Object fileDesc,
            String pattern,
            String marker,
            ref Object context,
            out String fileName,
            out FileInfo fileInfo)
        {
            FileNode fileNode = (FileNode)fileNode0;
            IEnumerator<String> enumerator = (IEnumerator<String>)context;

            if (null == enumerator)
            {
                List<String> childrenFileNames = new List<String>();
                if ("\\" != fileNode.FileName)
                {
                    /* if this is not the root directory add the dot entries */
                    if (null == marker)
                        childrenFileNames.Add(".");
                    if (null == marker || "." == marker)
                        childrenFileNames.Add("..");
                }
                childrenFileNames.AddRange(_fileNodeMap.GetChildrenFileNames(fileNode,
                    "." != marker && ".." != marker ? marker : null));
                context = enumerator = childrenFileNames.GetEnumerator();
            }

            while (enumerator.MoveNext())
            {
                String fullFileName = enumerator.Current;
                if ("." == fullFileName)
                {
                    fileName = ".";
                    fileInfo = fileNode.GetFileInfo();
                    return true;
                }
                else if (".." == fullFileName)
                {
                    Int32 result = STATUS_SUCCESS;
                    FileNode parentNode = _fileNodeMap.GetParent(fileNode.FileName, ref result);
                    if (null != parentNode)
                    {
                        fileName = "..";
                        fileInfo = parentNode.GetFileInfo();
                        return true;
                    }
                }
                else
                {
                    FileNode childFileNode = _fileNodeMap.Get(fullFileName);
                    if (null != childFileNode)
                    {
                        fileName = Path.GetFileName(fullFileName);
                        fileInfo = childFileNode.GetFileInfo();
                        return true;
                    }
                }
            }

            fileName = default(String);
            fileInfo = default(FileInfo);
            return false;
        }

        public override int GetDirInfoByName(
            Object parentNode0,
            Object fileDesc,
            String fileName,
            out String normalizedName,
            out FileInfo fileInfo)
        {
            FileNode parentNode = (FileNode)parentNode0;

            fileName =
                parentNode.FileName +
                ("\\" == parentNode.FileName ? "" : "\\") +
                Path.GetFileName(fileName);

            var fileNode = _fileNodeMap.Get(fileName);
            if (null == fileNode)
            {
                normalizedName = default(String);
                fileInfo = default(FileInfo);
                return STATUS_OBJECT_NAME_NOT_FOUND;
            }

            normalizedName = Path.GetFileName(fileNode.FileName);
            fileInfo = fileNode.FileInfo;

            return STATUS_SUCCESS;
        }

        public override Int32 GetReparsePointByName(
            String fileName,
            Boolean isDirectory,
            ref Byte[] reparseData)
        {
            var fileNode = _fileNodeMap.Get(fileName);
            if (null == fileNode)
                return STATUS_OBJECT_NAME_NOT_FOUND;

            if (0 == (fileNode.FileInfo.FileAttributes & (UInt32)FileAttributes.ReparsePoint))
                return STATUS_NOT_A_REPARSE_POINT;

            reparseData = fileNode.ReparseData;

            return STATUS_SUCCESS;
        }

        public override Int32 GetReparsePoint(
            Object fileNode0,
            Object fileDesc,
            String fileName,
            ref Byte[] reparseData)
        {
            FileNode fileNode = (FileNode)fileNode0;

            if (null != fileNode.MainFileNode)
                fileNode = fileNode.MainFileNode;

            if (0 == (fileNode.FileInfo.FileAttributes & (UInt32)FileAttributes.ReparsePoint))
                return STATUS_NOT_A_REPARSE_POINT;

            reparseData = fileNode.ReparseData;

            return STATUS_SUCCESS;
        }

        public override Int32 SetReparsePoint(
            Object fileNode0,
            Object fileDesc,
            String fileName,
            Byte[] reparseData)
        {
            FileNode fileNode = (FileNode)fileNode0;

            if (null != fileNode.MainFileNode)
                fileNode = fileNode.MainFileNode;

            if (_fileNodeMap.HasChild(fileNode))
                return STATUS_DIRECTORY_NOT_EMPTY;

            if (null != fileNode.ReparseData)
            {
                Int32 result = CanReplaceReparsePoint(fileNode.ReparseData, reparseData);
                if (0 > result)
                    return result;
            }

            fileNode.FileInfo.FileAttributes |= (UInt32)FileAttributes.ReparsePoint;
            fileNode.FileInfo.ReparseTag = GetReparseTag(reparseData);
            fileNode.ReparseData = reparseData;

            return STATUS_SUCCESS;
        }

        public override Int32 DeleteReparsePoint(
            Object fileNode0,
            Object fileDesc,
            String fileName,
            Byte[] reparseData)
        {
            FileNode fileNode = (FileNode)fileNode0;

            if (null != fileNode.MainFileNode)
                fileNode = fileNode.MainFileNode;

            if (null != fileNode.ReparseData)
            {
                Int32 result = CanReplaceReparsePoint(fileNode.ReparseData, reparseData);
                if (0 > result)
                    return result;
            }
            else
                return STATUS_NOT_A_REPARSE_POINT;

            fileNode.FileInfo.FileAttributes &= ~(UInt32)FileAttributes.ReparsePoint;
            fileNode.FileInfo.ReparseTag = 0;
            fileNode.ReparseData = null;

            return STATUS_SUCCESS;
        }

        public override Boolean GetStreamEntry(
            Object fileNode0,
            Object fileDesc,
            ref Object context,
            out String streamName,
            out UInt64 streamSize,
            out UInt64 streamAllocationSize)
        {
            FileNode fileNode = (FileNode)fileNode0;
            IEnumerator<String> enumerator = (IEnumerator<String>)context;

            if (null == enumerator)
            {
                if (null != fileNode.MainFileNode)
                    fileNode = fileNode.MainFileNode;

                List<String> streamFileNames = new List<String>();
                if (0 == (fileNode.FileInfo.FileAttributes & (UInt32)FileAttributes.Directory))
                    streamFileNames.Add(fileNode.FileName);
                streamFileNames.AddRange(_fileNodeMap.GetStreamFileNames(fileNode));
                context = enumerator = streamFileNames.GetEnumerator();
            }

            while (enumerator.MoveNext())
            {
                String fullFileName = enumerator.Current;
                FileNode streamFileNode = _fileNodeMap.Get(fullFileName);
                if (null != streamFileNode)
                {
                    int index = fullFileName.IndexOf(':');
                    streamName = 0 > index ? "" : fullFileName.Substring(index + 1);
                    streamSize = streamFileNode.FileInfo.FileSize;
                    streamAllocationSize = streamFileNode.FileInfo.AllocationSize;
                    return true;
                }
            }

            streamName = default(String);
            streamSize = default(UInt64);
            streamAllocationSize = default(UInt64);
            return false;
        }

        private readonly FileNodeMap _fileNodeMap;
        private readonly UInt32 _maxFileNodes;
        private readonly UInt32 _maxFileSize;
        private String _volumeLabel;
    }
}