﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace FreeMove
{
    class IOHelper
    {
        #region SymLink
        //External dll functions
        [DllImport("kernel32.dll")]
        static extern bool CreateSymbolicLink(
        string lpSymlinkFileName, string lpTargetFileName, SymbolicLink dwFlags);

        enum SymbolicLink
        {
            File = 0,
            Directory = 1
        }

        private bool MakeLink(string directory, string symlink)
        {
            return CreateSymbolicLink(symlink, directory, SymbolicLink.Directory);
        }
        #endregion

        public static Exception[] CheckDirectories(string source, string destination, bool safeMode)
        {
            List<Exception> exceptions = new List<Exception>();
            //Check for correct file path format
            try
            {
                Path.GetFullPath(source);
                Path.GetFullPath(destination);
            }
            catch (Exception e)
            {
                exceptions.Add(new Exception("Invalid path", e));
            }
            string pattern = @"^[A-Za-z]:\\{1,2}";
            if (!Regex.IsMatch(source, pattern) || !Regex.IsMatch(destination, pattern))
            {
                exceptions.Add(new Exception("Invalid path format"));
            }

            //Check if the chosen directory is blacklisted
            string[] Blacklist = { @"C:\Windows", @"C:\Windows\System32", @"C:\Windows\Config", @"C:\ProgramData" };
            foreach (string item in Blacklist)
            {
                if (source == item)
                {
                    exceptions.Add(new Exception($"The \"{source}\" directory cannot be moved."));
                }
            }

            //Check if folder is critical
            if (safeMode && (
                source == Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) ||
                source == Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)))
            {
                exceptions.Add(new Exception($"It's recommended not to move the {source} directory, you can disable safe mode in the Settings tab to override this check"));
            }

            //Check for existence of directories
            if (!Directory.Exists(source))
                exceptions.Add(new Exception("Source folder does not exist"));
            
            if (Directory.Exists(destination))
                exceptions.Add(new Exception("Destination folder already contains a folder with the same name"));

            try
            {
                if (!Directory.Exists(Directory.GetParent(destination).FullName))
                    exceptions.Add(new Exception("Destination folder does not exist"));
            }
            catch(Exception e)
            {
                exceptions.Add(e);
            }

            // Next checks rely on the previous so if there was any exception return
            if (exceptions.Count > 0)
                return exceptions.ToArray();

            //Check admin privileges
            string TestFile = Path.Combine(Path.GetDirectoryName(source), "deleteme");
            int ti;
            for (ti = 0; File.Exists(TestFile + ti.ToString()) ; ti++); // Change name if a file with the same name already exists
            TestFile += ti.ToString();

            try
            {
                System.Security.AccessControl.DirectorySecurity ds = Directory.GetAccessControl(source);
                //Try creating a file to check permissions
                File.Create(TestFile).Close();
            }
            catch (UnauthorizedAccessException e)
            {
                exceptions.Add(new Exception("You do not have the required privileges to move the directory.\nTry running as administrator", e));
            }
            finally
            {
                if (File.Exists(TestFile))
                    File.Delete(TestFile);
            }

            //Try creating a symbolic link to check permissions
            try
            {
                if (!CreateSymbolicLink(TestFile, Path.GetDirectoryName(destination), SymbolicLink.Directory))
                    exceptions.Add(new Exception("Could not create a symbolic link.\nTry running as administrator"));
            }
            finally
            {
                if (Directory.Exists(TestFile))
                    Directory.Delete(TestFile);
            }

            // Next checks rely on the previous so if there was any exception return
            if (exceptions.Count > 0)
                return exceptions.ToArray();

            long size = 0;
            DirectoryInfo dirInf = new DirectoryInfo(source);
            foreach (FileInfo file in dirInf.GetFiles("*", SearchOption.AllDirectories))
            {
                size += file.Length;
            }
            DriveInfo dstDrive = new DriveInfo(Path.GetPathRoot(destination));
            if (dstDrive.AvailableFreeSpace < size)
                exceptions.Add(new Exception($"There is not enough free space on the {dstDrive.Name} disk. {size / 1000000}MB required, {dstDrive.AvailableFreeSpace / 1000000} available."));

            //If set to do full check try to open for write all files
            if (Settings.PermCheck)
            {
                try
                {
                    Parallel.ForEach(Directory.GetFiles(source), file =>
                    {
                        CheckFile(file);
                    });
                    Parallel.ForEach(Directory.GetDirectories(source), dir =>
                    {
                        Parallel.ForEach(Directory.GetFiles(dir), file =>
                        {
                            CheckFile(file);
                        });
                    });
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                }

                void CheckFile(string file)
                {
                    FileInfo fi = new FileInfo(file);
                    FileStream fs = null;
                    try
                    {
                        fs = fi.Open(FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                    }
                    catch (Exception ex)
                    {
                        exceptions.Add(ex);
                    }
                    finally
                    {
                        if (fs != null)
                            fs.Dispose();
                    }
                }
            }
            return exceptions.ToArray();
        }

        public static Task MoveDirectory(string dirFrom, string dirTo, CancellationToken ct)
        {
            return Task.Run(() =>
            {
                CopyDirectory(dirFrom, dirTo, ct);
                Directory.Delete(dirFrom);
            });
        }

        private static IOResult<string> CopyDirectory(string dirFrom, string dirTo, CancellationToken ct)
        {
            if (!Directory.Exists(dirTo))
                Directory.CreateDirectory(dirTo);
            string[] files = Directory.GetFiles(dirFrom);
            foreach (string file in files)
            {
                if (ct.IsCancellationRequested)
                    ct.ThrowIfCancellationRequested();

                string name = Path.GetFileName(file);
                string dest = Path.Combine(dirTo, name);
                if (!File.Exists(dest))
                    File.Copy(file, dest);
            }
            string[] folders = Directory.GetDirectories(dirFrom);
            foreach (string folder in folders)
            {
                string name = Path.GetFileName(folder);
                string dest = Path.Combine(dirTo, name);
                CopyDirectory(folder, dest, ct);
            }
            return IOResult<string>.Ok();
        }
    }
}