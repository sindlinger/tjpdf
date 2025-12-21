using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace FilterPDF
{
    /// <summary>
    /// Gerenciador de saída para console ou arquivo com suporte a paginação automática
    /// </summary>
    public class OutputManager : IDisposable
    {
        private TextWriter originalConsoleOut;
        private StreamWriter? fileWriter;
        private bool isWritingToFile;
        private StringBuilder outputBuffer;
        private int terminalHeight;
        private const int LINES_THRESHOLD = 20; // Usar paginação se mais de 20 linhas
        
        public OutputManager(Dictionary<string, string> outputOptions)
        {
            originalConsoleOut = Console.Out;
            isWritingToFile = false;
            outputBuffer = new StringBuilder();
            
            // Detectar altura do terminal
            try
            {
                terminalHeight = Console.WindowHeight;
            }
            catch
            {
                terminalHeight = 24; // Default fallback
            }
            
            // Debug statements removed
            
            
            // Verificar se deve redirecionar para arquivo
            string? outputFile = null;
            
            if (outputOptions.ContainsKey("-o"))
            {
                outputFile = MakeAbsolutePath(outputOptions["-o"]);
            }
            else if (outputOptions.ContainsKey("--output"))
            {
                outputFile = MakeAbsolutePath(outputOptions["--output"]);
            }
            else if (outputOptions.ContainsKey("--output-file"))
            {
                outputFile = MakeAbsolutePath(outputOptions["--output-file"]);
            }
            else if (outputOptions.ContainsKey("--output-dir"))
            {
                // Para --output-dir, validar se é um diretório válido
                string outputDirPath = outputOptions["--output-dir"];
                
                // Detectar se o usuário passou um nome de arquivo em vez de diretório
                if (HasFileExtension(outputDirPath))
                {
                    originalConsoleOut.WriteLine($"Warning: --output-dir expects a directory, but '{outputDirPath}' looks like a filename.");
                    originalConsoleOut.WriteLine($"Did you mean to use: -o \"{outputDirPath}\" instead?");
                    originalConsoleOut.WriteLine($"Creating directory '{outputDirPath}' and generating timestamped filename...");
                    originalConsoleOut.WriteLine();
                }
                
                // Sempre criar o diretório se não existir
                if (!Directory.Exists(outputDirPath))
                {
                    Directory.CreateDirectory(outputDirPath);
                }
                
                string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                outputFile = Path.Combine(outputDirPath, $"filter_results_{timestamp}.txt");
            }
            
            if (!string.IsNullOrEmpty(outputFile))
            {
                try
                {
                    // Criar diretório se não existir
                    string? directory = Path.GetDirectoryName(outputFile);
                    if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                    {
                        Directory.CreateDirectory(directory);
                    }
                    
                    fileWriter = new StreamWriter(outputFile, false, System.Text.Encoding.UTF8);
                    Console.SetOut(fileWriter);
                    isWritingToFile = true;
                    
                    // Mostrar no console original onde o arquivo está sendo salvo
                    // Usar caminho relativo ou absoluto conforme fornecido
                    string displayPath = outputFile;
                    
                    // Se o caminho é relativo, manter como está
                    if (!Path.IsPathRooted(outputFile))
                    {
                        displayPath = outputFile;
                    }
                    else
                    {
                        // Se é absoluto, converter para formato Unix se estiver em ambiente Unix/Linux
                        displayPath = NormalizePath(outputFile);
                    }
                    
                    originalConsoleOut.WriteLine($"Output will be saved to: {displayPath}");
                }
                catch (Exception ex)
                {
                    originalConsoleOut.WriteLine($"Error: Could not create output file '{outputFile}': {ex.Message}");
                    originalConsoleOut.WriteLine("Output will be displayed on console instead.");
                }
            }
            else
            {
                // Se não está salvando em arquivo, considerar usar paginação
                // Redirecionar Console.Out para o buffer temporariamente
                Console.SetOut(new StringWriter(outputBuffer));
            }
        }
        
        public void Dispose()
        {
            if (isWritingToFile)
            {
                // Garantir que todo conteúdo seja escrito
                if (fileWriter != null)
                {
                    try
                    {
                        fileWriter.Flush();
                        fileWriter.Close();
                    }
                    catch (Exception ex)
                    {
                        originalConsoleOut.WriteLine($"Error writing to output file: {ex.Message}");
                    }
                    finally
                    {
                        fileWriter.Dispose();
                    }
                }
                
                Console.SetOut(originalConsoleOut);
            }
            else
            {
                // Restaurar console original
                Console.SetOut(originalConsoleOut);
                
                // Processar saída com paginação se necessário
                ProcessOutputWithPagination();
            }
        }
        
        private void ProcessOutputWithPagination()
        {
            string output = outputBuffer.ToString();
            if (string.IsNullOrEmpty(output))
                return;
                
            string[] lines = output.Split('\n');
            
            // Se o output for pequeno ou estivermos em ambiente não interativo, mostrar direto
            if (lines.Length <= LINES_THRESHOLD || !IsInteractiveTerminal())
            {
                originalConsoleOut.Write(output);
                return;
            }
            
            // Tentar usar paginador (less, more, etc.)
            if (TryUsePager(output))
            {
                return;
            }
            
            // Fallback: paginação manual simples
            ShowWithManualPagination(lines);
        }
        
        private bool IsInteractiveTerminal()
        {
            try
            {
                // Verificar se estamos em um terminal interativo
                return !Console.IsOutputRedirected && !Console.IsInputRedirected;
            }
            catch
            {
                return false;
            }
        }
        
        private bool TryUsePager(string content)
        {
            try
            {
                string? pager = Environment.GetEnvironmentVariable("PAGER") ?? GetDefaultPager();
                
                if (string.IsNullOrEmpty(pager))
                    return false;
                
                var processInfo = new ProcessStartInfo
                {
                    FileName = pager,
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = false,
                    RedirectStandardError = false
                };
                
                var process = Process.Start(processInfo);
                if (process != null)
                {
                    using (var writer = process.StandardInput)
                    {
                        writer.Write(content);
                    }
                    
                    process.WaitForExit();
                    return true;
                }
            }
            catch
            {
                // Falhou ao usar paginador externo
            }
            
            return false;
        }
        
        private string? GetDefaultPager()
        {
            // Verificar paginadores disponíveis em ordem de preferência
            string[] pagers = { "less", "more", "cat" };
            
            foreach (string pager in pagers)
            {
                if (IsCommandAvailable(pager))
                {
                    // Configurações específicas para cada paginador
                    switch (pager)
                    {
                        case "less":
                            return "less -R"; // -R para preservar cores
                        case "more":
                            return "more";
                        default:
                            return pager;
                    }
                }
            }
            
            return null;
        }
        
        private bool IsCommandAvailable(string command)
        {
            try
            {
                var processInfo = new ProcessStartInfo
                {
                    FileName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "where" : "which",
                    Arguments = command,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                
                using (var process = Process.Start(processInfo))
                {
                    if (process != null)
                    {
                        process.WaitForExit();
                        return process.ExitCode == 0;
                    }
                    return false;
                }
            }
            catch
            {
                return false;
            }
        }
        
        private void ShowWithManualPagination(string[] lines)
        {
            int currentLine = 0;
            int pageSize = terminalHeight - 2; // Deixar espaço para prompt
            
            while (currentLine < lines.Length)
            {
                // Mostrar uma página
                int endLine = Math.Min(currentLine + pageSize, lines.Length);
                
                for (int i = currentLine; i < endLine; i++)
                {
                    originalConsoleOut.WriteLine(lines[i]);
                }
                
                currentLine = endLine;
                
                // Se ainda há mais conteúdo, mostrar prompt
                if (currentLine < lines.Length)
                {
                    originalConsoleOut.Write($"-- More -- ({currentLine}/{lines.Length} lines) [Press ENTER for next page, 'q' to quit]: ");
                    
                    string? input = Console.ReadLine();
                    if (input?.ToLower().Trim() == "q")
                    {
                        break;
                    }
                }
            }
        }
        
        /// <summary>
        /// Escreve uma mensagem no console original (mesmo quando redirecionando para arquivo)
        /// </summary>
        public void WriteToConsole(string message)
        {
            originalConsoleOut.WriteLine(message);
        }
        
        /// <summary>
        /// Força flush da saída
        /// </summary>
        public void Flush()
        {
            if (isWritingToFile && fileWriter != null)
            {
                fileWriter.Flush();
            }
            else
            {
                Console.Out.Flush();
            }
        }
        
        /// <summary>
        /// Detecta se um caminho parece ser um arquivo (tem extensão)
        /// </summary>
        private bool HasFileExtension(string path)
        {
            if (string.IsNullOrEmpty(path))
                return false;
                
            // Verificar se tem extensão comum de arquivo
            string extension = Path.GetExtension(path).ToLower();
            
            // Lista de extensões comuns que indicam arquivo
            string[] fileExtensions = { ".txt", ".json", ".xml", ".csv", ".md", ".log", ".out", ".dat" };
            
            return !string.IsNullOrEmpty(extension) && Array.Exists(fileExtensions, ext => ext == extension);
        }
        
        /// <summary>
        /// Converte caminho relativo para absoluto baseado no diretório atual
        /// Normaliza para o formato correto do sistema operacional
        /// </summary>
        private string MakeAbsolutePath(string path)
        {
            
            if (string.IsNullOrEmpty(path))
                return path;

            // Detectar ambiente uma única vez
            bool isWSL = Environment.GetEnvironmentVariable("WSL_DISTRO_NAME") != null;
            bool isLinux = RuntimeInformation.IsOSPlatform(OSPlatform.Linux);
            // Detectar WSL também pela presença do diretório /mnt (mais confiável)
            bool hasWSLMount = Directory.Exists("/mnt");
            // Detectar se o caminho atual contém /mnt/ (forte indicador de WSL)
            bool currentDirIsWSL = Directory.GetCurrentDirectory().StartsWith("/mnt/");
            bool isUnixLike = isLinux || isWSL || hasWSLMount || currentDirIsWSL ||
                             Environment.OSVersion.Platform == PlatformID.Unix || 
                             Environment.OSVersion.Platform == PlatformID.MacOSX;
                
            // Se já é absoluto, normalizar para o sistema atual
            if (Path.IsPathRooted(path))
            {
                
                // Em Linux/WSL, converter para formato Unix
                if (isUnixLike)
                {
                    string result = path.Replace('\\', '/');
                    return result;
                }
                // No Windows puro, manter formato Windows
                return path;
            }
                
            // Para caminhos relativos, usar Path.GetFullPath mas normalizar resultado
            string fullPath = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), path));
            
            // Em Linux/WSL, normalizar para formato Unix
            if (isUnixLike)
            {
                string result = fullPath.Replace('\\', '/');
                return result;
            }
            
            // No Windows puro, retornar como está
            return fullPath;
        }
        
        /// <summary>
        /// Normaliza caminhos para formato Unix/Linux quando apropriado
        /// </summary>
        private string NormalizePath(string path)
        {
            if (string.IsNullOrEmpty(path))
                return path;
                
            // Se estamos em ambiente Unix/Linux (incluindo WSL)
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) || 
                RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                // Converter barras invertidas do Windows para barras normais
                path = path.Replace('\\', '/');
                
                // Remover letras de drive do Windows se existirem (ex: C:, D:, etc)
                if (path.Length >= 2 && char.IsLetter(path[0]) && path[1] == ':')
                {
                    // Converter C:\path\to\file para /mnt/c/path/to/file (formato WSL)
                    if (Environment.GetEnvironmentVariable("WSL_DISTRO_NAME") != null)
                    {
                        char driveLetter = char.ToLower(path[0]);
                        path = $"/mnt/{driveLetter}{path.Substring(2)}";
                    }
                    else
                    {
                        // Em Linux/Mac puro, remover drive letter
                        path = path.Substring(2);
                    }
                }
            }
            
            return path;
        }
    }
}