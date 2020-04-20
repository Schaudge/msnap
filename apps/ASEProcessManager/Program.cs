﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ASELib;
using System.IO;
using System.Diagnostics;
using System.Runtime.Remoting.Messaging;
using System.Linq.Expressions;

namespace ASEProcessManager
{
    //
    // The ASEProcessManager is responsible for driving the process of going from data stored in TCGA to a completed analysis.  There are two basic abstrations: Cases (patients with tumors
    // and apropriate data) and ProcessingStages, which are procedures that take a Case or set of Cases and run some analysis on it to produce more data.  Thus, the ProcessingStages
    // connect together to form the data flow graph for the system.  Each run of ASEProcessManager looks in the file system to find the state of the world and then generates scripts to
    // run processes that move things along toward a completed state.  These scripts will either download data from the Genome Data Commons or will read in existing data and do some processing
    // on it in order to produce an output file.
    //
    // So the overall way of running the complete experiment is to run ASEProcessManager, run the script that it produces, and repeat until ASEProcessManager says that all of the work is done.
    //
    class Program
    {
        const string scriptFilename = "ASENextSteps.cmd";
        const string linuxScriptFilename = "ASENextStepsLinux";
        const string downloadScriptFilename = "ASEDownload.cmd";

        static string jobAddString = "";

        //
        // A ProcessingStage examines the state of the world and if the prerequisites are there and the step isn't completed adds commands to the script to do some
        // work and advance the state of the world.
        //
        interface ProcessingStage
        {
            string GetStageName();
            bool NeedsCases();
            void EvaluateStage(StateOfTheWorld stateOfTheWorld, StreamWriter script, ASETools.RandomizingStreamWriter hpcScript, StreamWriter linuxScript, StreamWriter azureScript, out List<string> filesToDownload, out int nDone, out int nAddedToScript, out int nWaitingForPrerequisites);
            bool EvaluateDependencies(StateOfTheWorld stateOfTheWorld);
        }

        class FileIdAndExpectedMD5
        {
            public FileIdAndExpectedMD5(string fileId_, string expectedMD5_)
            {
                fileId = fileId_;
                expectedMD5 = expectedMD5_;
            }

            public readonly string fileId;
            public readonly string expectedMD5;

        }

        delegate string GetCaseFile(ASETools.Case case_);
        delegate string GetOneOffFile(StateOfTheWorld stateOfTheWorld);
        delegate string GetPerDiseaseFile(StateOfTheWorld stateOfTheWorld, string disease);
        delegate string GetPerChromosomePerDiseaseFile(StateOfTheWorld stateOfTheWorld, string chromosome, string disease);
        delegate string GenerateCaseIdOutput(string caseId, StateOfTheWorld stateOfTheWorld);
        delegate FileIdAndExpectedMD5 GetDownloadableInputFile(ASETools.Case case_, StateOfTheWorld stateOfTheWorld);
 
        class PerCaseProcessingStage : ProcessingStage  // a processing stage where an action is taken for every case.
        {
            string stageName;
            List<GetCaseFile> caseFileInputGetters;
            List<GetOneOffFile> oneOffFileGetters;
            List<GetCaseFile> outputFileGetters;
            GenerateCaseIdOutput generateCaseIdOutput;
            List<GetDownloadableInputFile> downloadableFileGetters;

            string binaryName;
            string parametersBeforeCaseIds;
            int maxCaseIdsPerLine;  // There is also a limit on the total length of the line in characters.
            int maxCharsPerLine;
            int desiredParallelism;
            bool needsCommonData;

            public PerCaseProcessingStage(string stageName_, string binaryName_, string parametersBeforeCaseIds_, GetCaseFile[] getCaseFile, GetOneOffFile[] getOneOffFile, GetCaseFile[] getOutputFile, int desiredParallelism_ = 0, int maxCaseIdsPerLine_ = int.MaxValue, int maxCharsPerLine_ = 5000,
                GenerateCaseIdOutput generateCaseIdOutput_ = null, GetDownloadableInputFile[] getDownloadableInputFiles = null, bool needsCommonData_ = false)
            {
                stageName = stageName_;
                binaryName = binaryName_;
                parametersBeforeCaseIds = parametersBeforeCaseIds_;
                desiredParallelism = desiredParallelism_;
                maxCaseIdsPerLine = maxCaseIdsPerLine_;
                maxCharsPerLine = maxCharsPerLine_;
                needsCommonData = needsCommonData_;

                caseFileInputGetters = getCaseFile == null ? null : getCaseFile.ToList();

                oneOffFileGetters = getOneOffFile == null ? null : getOneOffFile.ToList();

                outputFileGetters = getOutputFile == null ? new List<GetCaseFile>() : getOutputFile.ToList();

                downloadableFileGetters = getDownloadableInputFiles == null ? null : getDownloadableInputFiles.ToList();

                if (generateCaseIdOutput_ == null)
                {
                    generateCaseIdOutput = (caseId, stateOfTheWorld) => caseId; // Just directly use the case ID
                } else
                {
                    generateCaseIdOutput = generateCaseIdOutput_;
                }
            }

            public void addOutputFileGetter(GetCaseFile outputFileGetter)
            {
                outputFileGetters.Add(outputFileGetter);
            }

            public void replaceGenerateCaseIdOutput(GenerateCaseIdOutput generateCaseIdOutput_)
            {
                generateCaseIdOutput = generateCaseIdOutput_;
            }

            public void addDownloadableInputFile(GetDownloadableInputFile getDownloadableInputFile)
            {
                if (null == downloadableFileGetters)
                {
                    downloadableFileGetters = new List<GetDownloadableInputFile>();
                }

                downloadableFileGetters.Add(getDownloadableInputFile);
            } // addDownloadableInputFile

            public string GetStageName() { return stageName; }
            public bool NeedsCases() { return true; }

            bool caseIsDone(ASETools.Case case_)
            {
                return outputFileGetters == null || outputFileGetters.All(getter => getter != null && getter(case_) != "" && getter(case_) != null);
            }

            bool caseNeedsPrerequisites(StateOfTheWorld stateOfTheWorld, ASETools.Case case_)
            {
                if (outputFileGetters.All(_ => _ == null ||  _(case_) == null))
                {
                    return false;   // If the output file getters all return null (not the empty string) then this is a no-op, which consequently needs no prerequisites
                }

                if (downloadableFileGetters != null)
                {
                    foreach (var downloadableFileGetter in downloadableFileGetters)
                    {
                        var fileIdAndExpectedMD5 = downloadableFileGetter(case_, stateOfTheWorld);

                        if (fileIdAndExpectedMD5 != null && !stateOfTheWorld.fileDownloadedAndVerified(fileIdAndExpectedMD5.fileId, fileIdAndExpectedMD5.expectedMD5))
                        {
                            return true;    // Someone else will queue the download & verify, it's not our job.  We just wait for it to be done.
                        }
                    } // for each downloadable file needed
                } // If we have any downloadable file getters

                if (null == caseFileInputGetters)
                {
                    return false;
                }

                return caseFileInputGetters.Any(inputFileGetter => inputFileGetter(case_) == "");
            }

            public void EvaluateStage(StateOfTheWorld stateOfTheWorld, StreamWriter script, ASETools.RandomizingStreamWriter hpcScript, StreamWriter linuxScript, StreamWriter azureScript, out List<string> filesToDownload, out int nDone, out int nAddedToScript, out int nWaitingForPrerequisites)
            {
                nAddedToScript = 0;
                filesToDownload = null;

                nDone = stateOfTheWorld.listOfCases.Where(case_ => caseIsDone(case_)).Count();

                if (null != oneOffFileGetters && oneOffFileGetters.Any(getter => getter(stateOfTheWorld) == "" || !File.Exists(getter(stateOfTheWorld))))
                {
                    nWaitingForPrerequisites = 1;
                    return;
                }

                nWaitingForPrerequisites = stateOfTheWorld.listOfCases.Where(_ => caseNeedsPrerequisites(stateOfTheWorld, _) && !caseIsDone(_)).Count(); // Explicitly check for done here, so cases don't wind up both needing prereqs and done.
                nAddedToScript = 0;

                string outputLine = "";
                int nOnOutputLine = 0;

                var casesToRun = stateOfTheWorld.listOfCases.Where(case_ => !caseIsDone(case_) && !caseNeedsPrerequisites(stateOfTheWorld, case_) && outputFileGetters.All(outputFileGetter => outputFileGetter != null && outputFileGetter(case_) != null)).ToList();
                int nCasesToRun = casesToRun.Count();

                if (nCasesToRun == 0)
                {
                    return;
                }

                if (desiredParallelism == 0)
                {
                    desiredParallelism = stateOfTheWorld.configuration.nWorkerMachines;
                }

                //
                // Divide it up as equally as possible.
                //
                int realMaxPerLine = Math.Min(maxCaseIdsPerLine,
                    (maxCharsPerLine - (stateOfTheWorld.configuration.binariesDirectory + binaryName + stateOfTheWorld.configurationString + parametersBeforeCaseIds).Length) / (ASETools.GuidStringLength + 1)); // +1 is for the space between guids

                int minLines = (nCasesToRun + realMaxPerLine - 1) / realMaxPerLine;

                //
                // Now round up to a multiple of desiredParallelism
                //
                int desiredLines = ((minLines + desiredParallelism - 1) / desiredParallelism) * desiredParallelism;

                int nRemainingCases = nCasesToRun;
                int nCasesForThisLine = 0;  // This get set the first time through.
                int nOutputLinesWritten = 0;

                foreach (var case_ in casesToRun)
                {
                    if (nOnOutputLine == 0)
                    {
                        outputLine = stateOfTheWorld.configuration.binariesDirectory + binaryName + stateOfTheWorld.configurationString + " " + parametersBeforeCaseIds;
                        if (nOutputLinesWritten < desiredLines)
                        {
                            nCasesForThisLine = nRemainingCases / (desiredLines - nOutputLinesWritten);
                        } else
                        {
                            nCasesForThisLine = -1;
                        }
                    }

                    outputLine += " " + generateCaseIdOutput(case_.case_id, stateOfTheWorld);

                    nOnOutputLine++;

                    nCasesForThisLine--;

                    if (nOnOutputLine >= maxCaseIdsPerLine || outputLine.Length >= maxCharsPerLine || nCasesForThisLine == 0)
                    {
                        script.WriteLine(outputLine);
                        outputLine = "";
                        nOnOutputLine = 0;
                        nOutputLinesWritten++;
                    }

                    nAddedToScript++;
                    nRemainingCases--;
                } // foreach case

                if (nOnOutputLine != 0)
                {
                    script.WriteLine(outputLine);
                }
            } // EvaluateStage

            public bool EvaluateDependencies(StateOfTheWorld stateOfTheWorld)
            {
                bool allOK = true;

                var doneCases = stateOfTheWorld.cases.Select(_ => _.Value).Where(_ => caseIsDone(_)).ToList();

                if (doneCases.Count() == 0)
                {
                    return allOK;   // vaccuously true
                }

                DateTime newestPrerequisiteWriteTime = DateTime.MinValue;
                string newestPrerequisite = "";

                if (oneOffFileGetters != null)
                {
                    foreach (var prerequisiteFile in oneOffFileGetters.Select(getter => getter(stateOfTheWorld)))
                    {
                        if (!File.Exists(prerequisiteFile))
                        {
                            Console.WriteLine("Processing stage " + stageName + " has completed files, but is missing generic prerequisite " + prerequisiteFile);
                            allOK = false;
                        }

                        if (prerequisiteFile == stateOfTheWorld.configuration.redundantChromosomeRegionFilename)
                        {
                            //
                            // Just skip this one, it will never vary since it only depends on the genome itself.
                            //
                            continue;
                        }

                        var prerequisiteWriteTime = File.GetLastWriteTime(prerequisiteFile);
                        if (prerequisiteWriteTime > newestPrerequisiteWriteTime)
                        {
                            newestPrerequisiteWriteTime = prerequisiteWriteTime;
                            newestPrerequisite = prerequisiteFile;
                        }
                    } // foreach
                } // if there are one off file getters

                if (!allOK)
                {
                    return allOK;
                }

                foreach (var case_ in doneCases)
                {
                    if (!caseIsDone(case_))
                    {
                        continue;
                    }

                    foreach (var outputFile in outputFileGetters.Select(getter => getter(case_)).ToList())
                    {
                        if (!File.Exists(outputFile))
                        {
                            continue;
                        }

                        var outputFileLastWriteTime = File.GetLastWriteTime(outputFile);

                        if (outputFileLastWriteTime < newestPrerequisiteWriteTime)
                        {
                            Console.WriteLine("Processing stage " + stageName + " has generated file " + outputFile + " older than its input " + newestPrerequisite);
                            allOK = false;
                            continue;
                        }

                        if (caseFileInputGetters != null)
                        {
                            foreach (var inputGetter in caseFileInputGetters)
                            {
                                var inputFile = inputGetter(case_);
                                if (inputFile == "")
                                {
                                    Console.WriteLine("Processing stage " + stageName + " has generated file " + outputFile + " with a missing input.");
                                    allOK = false;
                                    continue;
                                }

                                if (!File.Exists(inputFile))
                                {
                                    Console.WriteLine("Processing stage " + stageName + " has generated file " + outputFile + " with missing input " + inputFile);
                                    allOK = false;
                                    continue;
                                }

                                if (outputFileLastWriteTime < File.GetLastWriteTime(inputFile))
                                {
                                    Console.WriteLine("Processing stage " + stageName + " has generated file " + outputFile + " older than input " + inputFile);
                                    allOK = false;
                                    continue;
                                }
                            } // input getter
                        } // if there are input getters at all
                    } // output file
                } // case

                return allOK;
            } // EvaluateDependencies

        } // PerCaseProcessingStage

        class PerDiseaseProcessingStage : ProcessingStage
        {
            string stageName;
            string binaryName;
            bool supplyConfigurationParameter;
            string parametersBeforeDiseaseName;
            List<GetCaseFile> getPerCaseInputs;
            List<GetPerDiseaseFile> getPerDiseaseInputs;
            List<GetOneOffFile> getOneOffInputs;
            List<GetPerDiseaseFile> getOutputs;

            public bool NeedsCases() { return true; }

            public PerDiseaseProcessingStage(string stageName_, string binaryName_, bool supplyConfiguratonParameter_, string parametersBeforeDiseaseName_, GetCaseFile[] getPerCaseInputs_, GetPerDiseaseFile[] getPerDiseaseInputs_,
                GetOneOffFile[] getOneOffInputs_, GetPerDiseaseFile[] getOutputs_)
            {
                stageName = stageName_;
                binaryName = binaryName_;
                supplyConfigurationParameter = supplyConfiguratonParameter_;
                parametersBeforeDiseaseName = parametersBeforeDiseaseName_;

                if (null == getPerCaseInputs_)
                {
                    getPerCaseInputs = new List<GetCaseFile>();
                }
                else
                {
                    getPerCaseInputs = getPerCaseInputs_.ToList();
                }

                if (null == getPerDiseaseInputs_)
                {
                    getPerDiseaseInputs = new List<GetPerDiseaseFile>();
                }
                else
                {
                    getPerDiseaseInputs = getPerDiseaseInputs_.ToList();
                }

                if (null == getOneOffInputs_)
                {
                    getOneOffInputs = new List<GetOneOffFile>();
                }
                else
                {
                    getOneOffInputs = getOneOffInputs_.ToList();
                }

                getOutputs = getOutputs_.ToList();
            }


            public string GetStageName() { return stageName; }

            public void EvaluateStage(StateOfTheWorld stateOfTheWorld, StreamWriter script, ASETools.RandomizingStreamWriter hpcScript, StreamWriter linuxScript, StreamWriter azureScript, out List<string> filesToDownload, out int nDone, out int nAddedToScript, out int nWaitingForPrerequisites)
            {
                nAddedToScript = 0;
                filesToDownload = null;

                nDone = stateOfTheWorld.diseases.Where(_ => diseaseIsDone(stateOfTheWorld, _)).Count();

                if (!getOneOffInputs.All(_ => !File.Exists(_(stateOfTheWorld))))
                {
                    nWaitingForPrerequisites = getOneOffInputs.Where(_ => !File.Exists(_(stateOfTheWorld))).Count();
                    return;
                }

                nWaitingForPrerequisites = 0;

                foreach (var disease in stateOfTheWorld.diseases)
                {
                    if (diseaseIsDone(stateOfTheWorld, disease))
                    {
                        continue;
                    }

                    if (getPerDiseaseInputs.Any(_ => !File.Exists(_(stateOfTheWorld, disease))))
                    {
                        nWaitingForPrerequisites++;
                        continue;
                    }

                    if (stateOfTheWorld.listOfCases.Where(case_ => case_.disease() == disease).Any(case_ => getPerCaseInputs.Any(perCaseInputGetter => perCaseInputGetter(case_) == "")))
                    {
                        nWaitingForPrerequisites++;
                    } else
                    {
                        script.WriteLine(stateOfTheWorld.configuration.binariesDirectory + binaryName + " " +(supplyConfigurationParameter ? stateOfTheWorld.configurationString : "") + parametersBeforeDiseaseName + " " + disease);
                        nAddedToScript++;
                    }
                } // disease
            } // EvaluateStage

            public bool EvaluateDependencies(StateOfTheWorld stateOfTheWorld)
            {
                bool anyFailed = false;

                foreach (var disease in stateOfTheWorld.diseases)
                {
                    if (!diseaseIsDone(stateOfTheWorld, disease))
                    {
                        continue;
                    }

                    foreach (var outputFile in getOutputs.Select(_ => _(stateOfTheWorld, disease)))
                    {
                        var outputDate = File.GetLastWriteTime(outputFile);

                        foreach (var oneOffInput in getOneOffInputs.Select(_ => _(stateOfTheWorld)))
                        {
                            if (!File.Exists(oneOffInput))
                            {
                                Console.WriteLine(stageName + ": one-off input " + oneOffInput + " doesn't exist, while output " + outputFile + " does.  Disease: " + disease);
                                anyFailed = true;
                                break;
                            }

                            if (File.GetLastWriteTime(oneOffInput) > outputDate)
                            {
                                Console.WriteLine(stageName + ": one off input " + oneOffInput + " is newer than output " + outputFile + ".  Disease: " + disease);
                                anyFailed = true;
                                break;
                            }
                        } // one off input

                        foreach (var perDiseaseInput in getPerDiseaseInputs.Select(_ => _(stateOfTheWorld, disease)))
                        {
                            if (!File.Exists(perDiseaseInput))
                            {
                                Console.WriteLine(stageName + ": output file " + outputFile + " exists, but per-disease input " + perDiseaseInput + " does not.  Disease: " + disease);
                                anyFailed = true;
                                break;
                            }

                            if (File.GetLastWriteTime(perDiseaseInput) > outputDate)
                            {
                                Console.WriteLine(stageName + ": output file " + outputFile + " is older than per-disease input file " + perDiseaseInput + ".  Disease: " + disease);
                                anyFailed = true;
                                break;
                            }
                        }

                        foreach (var perCaseInputGetter in getPerCaseInputs)
                        {
                            foreach (var case_ in stateOfTheWorld.listOfCases.Where(_ => _.disease() == disease))
                            {
                                string input = perCaseInputGetter(case_);

                                if (input == "")
                                {
                                    Console.WriteLine(stageName + ": output file " + outputFile + " exists, while a per-case input for case " + case_.case_id + " does not.  Disease: " + disease);
                                    anyFailed = true;
                                    break;
                                }

                                if (File.GetLastWriteTime(input) > outputDate)
                                {
                                    Console.WriteLine(stageName + ": output file " + outputFile + " is newer than per-case input file " + input + ".  Disease: " + disease);
                                    anyFailed = true;
                                    break;
                                }
                            } // case 
                        } // per case input
                    } // output file
                } // disease

                return !anyFailed;
            } // EvaluateDependencies

            bool diseaseIsDone(StateOfTheWorld stateOfTheWorld, string disease)
            {
                return getOutputs.All(_ => File.Exists(_(stateOfTheWorld, disease)));
            }
        } // PerDiseaseProcessingStage

        class PerChromosomePerDiseaseProcessingStage : ProcessingStage
        {
            string stageName;
            string binaryName;
            bool supplyConfigurationParameter;
            string parametersBeforeChromosomeAndDiseaseName;
            List<GetCaseFile> getPerCaseInputs;
            List<GetPerChromosomePerDiseaseFile> getPerDiseaseInputs;
            List<GetOneOffFile> getOneOffInputs;
            List<GetPerChromosomePerDiseaseFile> getOutputs;

            public bool NeedsCases() { return true; }

            public PerChromosomePerDiseaseProcessingStage(string stageName_, string binaryName_, bool supplyConfiguratonParameter_, string parametersBeforeChromosomeAndDiseaseName_, GetCaseFile[] getPerCaseInputs_, GetPerChromosomePerDiseaseFile[] getPerDiseaseInputs_,
                GetOneOffFile[] getOneOffInputs_, GetPerChromosomePerDiseaseFile[] getOutputs_)
            {
                stageName = stageName_;
                binaryName = binaryName_;
                supplyConfigurationParameter = supplyConfiguratonParameter_;
                parametersBeforeChromosomeAndDiseaseName = parametersBeforeChromosomeAndDiseaseName_;

                if (null == getPerCaseInputs_)
                {
                    getPerCaseInputs = new List<GetCaseFile>();
                }
                else
                {
                    getPerCaseInputs = getPerCaseInputs_.ToList();
                }

                if (null == getPerDiseaseInputs_)
                {
                    getPerDiseaseInputs = new List<GetPerChromosomePerDiseaseFile>();
                }
                else
                {
                    getPerDiseaseInputs = getPerDiseaseInputs_.ToList();
                }

                if (null == getOneOffInputs_)
                {
                    getOneOffInputs = new List<GetOneOffFile>();
                }
                else
                {
                    getOneOffInputs = getOneOffInputs_.ToList();
                }

                getOutputs = getOutputs_.ToList();
            }


            public string GetStageName() { return stageName; }

            public void EvaluateStage(StateOfTheWorld stateOfTheWorld, StreamWriter script, ASETools.RandomizingStreamWriter hpcScript, StreamWriter linuxScript, StreamWriter azureScript, out List<string> filesToDownload, out int nDone, out int nAddedToScript, out int nWaitingForPrerequisites)
            {
                nAddedToScript = 0;
                filesToDownload = null;

                nDone = ASETools.chromosomes.Sum(chromosome => stateOfTheWorld.diseases.Where(disease => chromosomeAndDiseaseIsDone(stateOfTheWorld, chromosome, disease)).Count());

                if (!getOneOffInputs.All(_ => File.Exists(_(stateOfTheWorld))))
                {
                    nWaitingForPrerequisites = getOneOffInputs.Where(_ => !File.Exists(_(stateOfTheWorld))).Count();
                    return;
                }

                nWaitingForPrerequisites = 0;

                foreach (var chromosome in ASETools.chromosomes)
                {
                    foreach (var disease in stateOfTheWorld.diseases)
                    {
                        if (chromosomeAndDiseaseIsDone(stateOfTheWorld, chromosome, disease))
                        {
                            continue;
                        }

                        if (getPerDiseaseInputs.Any(_ => _(stateOfTheWorld, chromosome, disease) == "" ||  !File.Exists(_(stateOfTheWorld, chromosome, disease))))
                        {
                            nWaitingForPrerequisites++;
                            continue;
                        }

                        if (stateOfTheWorld.listOfCases.Where(case_ => case_.disease() == disease).Any(case_ => getPerCaseInputs.Any(perCaseInputGetter => perCaseInputGetter(case_) == "")))
                        {
                            nWaitingForPrerequisites++;
                        }
                        else
                        {
                            script.WriteLine(stateOfTheWorld.configuration.binariesDirectory + binaryName + " " + (supplyConfigurationParameter ? stateOfTheWorld.configurationString : "") + parametersBeforeChromosomeAndDiseaseName + " " + chromosome + " " + disease);
                            nAddedToScript++;
                        }
                    } // disease
                } // chromosome
            } // EvaluateStage

            public bool EvaluateDependencies(StateOfTheWorld stateOfTheWorld)
            {
                bool anyFailed = false;

                foreach (var chromosome in ASETools.chromosomes)
                {
                    foreach (var disease in stateOfTheWorld.diseases)
                    {
                        if (!chromosomeAndDiseaseIsDone(stateOfTheWorld, chromosome, disease))
                        {
                            continue;
                        }

                        foreach (var outputFile in getOutputs.Select(_ => _(stateOfTheWorld, chromosome, disease)))
                        {
                            var outputDate = File.GetLastWriteTime(outputFile);

                            foreach (var oneOffInput in getOneOffInputs.Select(_ => _(stateOfTheWorld)))
                            {
                                if (!File.Exists(oneOffInput))
                                {
                                    Console.WriteLine(stageName + ": one-off input " + oneOffInput + " doesn't exist, while output " + outputFile + " does.  Disease: " + disease);
                                    anyFailed = true;
                                    break;
                                }

                                if (File.GetLastWriteTime(oneOffInput) > outputDate)
                                {
                                    Console.WriteLine(stageName + ": one off input " + oneOffInput + " is newer than output " + outputFile + ".  Disease: " + disease);
                                    anyFailed = true;
                                    break;
                                }
                            } // one off input

                            foreach (var perDiseaseInput in getPerDiseaseInputs.Select(_ => _(stateOfTheWorld, chromosome, disease)))
                            {
                                if (!File.Exists(perDiseaseInput))
                                {
                                    Console.WriteLine(stageName + ": output file " + outputFile + " exists, but per-chromosome per-disease input " + perDiseaseInput + " does not.  Disease: " + disease);
                                    anyFailed = true;
                                    break;
                                }

                                if (File.GetLastWriteTime(perDiseaseInput) > outputDate)
                                {
                                    Console.WriteLine(stageName + ": output file " + outputFile + " is older than per-chromosome per-disease input file " + perDiseaseInput + ".  Disease: " + disease);
                                    anyFailed = true;
                                    break;
                                }
                            }

                            foreach (var perCaseInputGetter in getPerCaseInputs)
                            {
                                foreach (var case_ in stateOfTheWorld.listOfCases.Where(_ => _.disease() == disease))
                                {
                                    string input = perCaseInputGetter(case_);

                                    if (input == "")
                                    {
                                        Console.WriteLine(stageName + ": output file " + outputFile + " exists, while a per-case input for case " + case_.case_id + " does not.  Disease: " + disease);
                                        anyFailed = true;
                                        break;
                                    }

                                    if (File.GetLastWriteTime(input) > outputDate)
                                    {
                                        Console.WriteLine(stageName + ": output file " + outputFile + " is newer than per-case input file " + input + ".  Disease: " + disease);
                                        anyFailed = true;
                                        break;
                                    }
                                } // case 
                            } // per case input
                        } // output file
                    } // disease
                } // chromosome

                return !anyFailed;
            } // EvaluateDependencies

            bool chromosomeAndDiseaseIsDone(StateOfTheWorld stateOfTheWorld, string chromosome, string disease)
            {
                return getOutputs.All(_ => _(stateOfTheWorld, chromosome, disease) != "" &&  File.Exists(_(stateOfTheWorld, chromosome, disease)));
            }
        } // PerDiseasePerChromosomeProcessingStage

        class SingleOutputProcessingStage : ProcessingStage
        {
            string stageName;
            List<GetCaseFile> caseFileInputGetters;
            List<GetOneOffFile> oneOffFileGetters;
            List<GetOneOffFile> outputFileGetters;

            string binaryName;
            string parameters;
            bool needsCases;
            bool needsCommonData;

            public SingleOutputProcessingStage(string stageName_, bool needsCases_, string binaryName_, string parameters_, GetCaseFile[] getCaseFile, GetOneOffFile[] getOneOffFile, GetOneOffFile[] getOutputFile, bool needsCommonData_ = false)
            {
                stageName = stageName_;
                needsCases = needsCases_;
                binaryName = binaryName_;
                parameters = parameters_;

                caseFileInputGetters = (getCaseFile == null) ? null : getCaseFile.ToList();

                oneOffFileGetters = (getOneOffFile == null) ? null : getOneOffFile.ToList();

                outputFileGetters = getOutputFile.ToList();

                needsCommonData = needsCommonData_;
            } // ctor

            public string GetStageName() { return stageName; }
            public bool NeedsCases() { return needsCases; }

            public void EvaluateStage(StateOfTheWorld stateOfTheWorld, StreamWriter script, ASETools.RandomizingStreamWriter hpcScript, StreamWriter linuxScript, StreamWriter azureScript, out List<string> filesToDownload, out int nDone, out int nAddedToScript, out int nWaitingForPrerequisites)
            {
                nWaitingForPrerequisites = 0;
                nDone = 0;
                nAddedToScript = 0;
                filesToDownload = null;

                if (outputFileGetters.All(_ => File.Exists(_(stateOfTheWorld))))
                {
                    nDone = 1;
                    return;
                }

                if (caseFileInputGetters != null && caseFileInputGetters.Any(inputGetter => stateOfTheWorld.listOfCases.Any(case_ => inputGetter(case_) == "")) || needsCommonData && !stateOfTheWorld.hasCommonData())
                {
                    nWaitingForPrerequisites = 1;
                    return;
                }

                if (oneOffFileGetters != null && oneOffFileGetters.Any(_ => !File.Exists(_(stateOfTheWorld))))
                {
                    nWaitingForPrerequisites = 1;
                    return;
                }

                nAddedToScript = 1;
                script.WriteLine(stateOfTheWorld.configuration.binariesDirectory + binaryName + " " + parameters);               
            } // EvaluateStage

            public bool EvaluateDependencies(StateOfTheWorld stateOfTheWorld)
            {
                if (outputFileGetters.Any(_ => !File.Exists(_(stateOfTheWorld))))
                {
                    return true;    // Output doesn't exist, so no dependencies.
                }

                DateTime commonDataDate = DateTime.Now; // If it doesn't need common data, this is newer than any files, so it will never trigger.

                if (needsCommonData)
                {
                    if (!stateOfTheWorld.hasCommonData() || stateOfTheWorld.commonData.GetInputFiles().Any(_ => !File.Exists(_)))
                    {
                        Console.WriteLine("Processing stage " + stageName + " depends on common data, but some of it is missing.");
                        return false;
                    }

                    foreach (var commonDataFilename in stateOfTheWorld.commonData.GetInputFiles())
                    {
                        DateTime fileWriteDate;
                        if ((fileWriteDate = File.GetLastWriteTime(commonDataFilename)) < commonDataDate)
                        {
                            commonDataDate = fileWriteDate;
                        }
                    }

                } // needsCommonData

                if (oneOffFileGetters == null && caseFileInputGetters == null)
                {
                    return true;    // There is no input, so it can't be stale
                }

                DateTime oldestOutput = DateTime.MaxValue;
                var oldestOutputFilename = "";

                foreach (var outputGetter in outputFileGetters)
                {
                    DateTime outputFileTime;
                    if ((outputFileTime = File.GetLastWriteTime(outputGetter(stateOfTheWorld))) < oldestOutput)
                    {
                        oldestOutputFilename = outputGetter(stateOfTheWorld);
                        oldestOutput = File.GetLastWriteTime(oldestOutputFilename);
                    }

                    if (outputFileTime > commonDataDate)
                    {
                        Console.WriteLine("Processing stage " + stageName + " has output file " + outputGetter(stateOfTheWorld) + " that is newer than at least one common data input.");
                        return false;
                    }
                }

                if (oneOffFileGetters != null)
                {
                    var missingInputFiles = oneOffFileGetters.Where(_ => !File.Exists(_(stateOfTheWorld))).Select(_ => _(stateOfTheWorld)).ToList();
                    if (missingInputFiles.Count() > 0)
                    {
                        Console.Write("Processing stage " + stageName + " has all output files but is missing these intput files:");
                        foreach (var missingInputFile in missingInputFiles)
                        {
                            Console.Write(" " + missingInputFile);
                        }
                        Console.WriteLine();
                        return false;
                    }

                    foreach (var oneOffGetter in oneOffFileGetters)
                    {
                        if (File.GetLastWriteTime(oneOffGetter(stateOfTheWorld)) < oldestOutput)
                        {
                            Console.WriteLine("Processing stage " + stageName + " has output " + oldestOutputFilename + " older than input " + oneOffGetter(stateOfTheWorld));
                            return false;
                        }
                    }
                }


                if (caseFileInputGetters != null) {
                    foreach (var case_ in stateOfTheWorld.listOfCases)
                    {
                        foreach (var caseFileGetter in caseFileInputGetters)
                        {
                            if (!File.Exists(caseFileGetter(case_)))
                            {
                                Console.WriteLine("Processing stage " + stageName + " has output (oldest of which is " + oldestOutputFilename + "), but is missing input file " + caseFileGetter(case_));
                                return false;
                            }
                            else 
                            {
                                if (File.GetLastWriteTime(caseFileGetter(case_)) > oldestOutput)
                                {
                                    Console.WriteLine("Processing stage " + stageName + " has output (oldest of which is " + oldestOutputFilename + ") that is older than input " + caseFileGetter(case_));
                                    return false;
                                } 
                            } // file exists
                        } // case file getter
                    } // case
                } // if there are any case file getters

                return true;
            } // EvaluateDependencies


        } // SingleOutputProcessingStage

        class SpliceosomeAllelicImbalanceProcessingStage : SingleOutputProcessingStage
        {
            public SpliceosomeAllelicImbalanceProcessingStage() : base("Spliceosome Allelic Imbalance", true, "SpliceosomeAllelicImbalance.exe", "", getCaseFiles, getOneOffFiles, getOutputFiles)
            {
            }

            static GetCaseFile[] getCaseFiles = { _ => _.isoform_read_counts_filename };
            static GetOneOffFile[] getOneOffFiles = { _ => _.configuration.finalResultsDirectory + ASETools.ASECorrectionFilename };    // Not clear it actually needs this, but it loads it with common data.
            static GetOneOffFile[] getOutputFiles = {_ => _.configuration.finalResultsDirectory + ASETools.IsoformBalancePValueHistogramFilename , _ => _.configuration.finalResultsDirectory + ASETools.IsoformBalanceFilenameBase + "_tumor.txt",
            _ => _.configuration.finalResultsDirectory + ASETools.IsoformBalanceFilenameBase + "_normal.txt"};  // XXX should be a way to do the per-disease and per-gene files here, too.
        }



        class ExtractIsoformReadCountsProcessingStage : PerCaseProcessingStage
        {
            public ExtractIsoformReadCountsProcessingStage(StateOfTheWorld stateOfTheWorld) : base("Extract Isoform Read Counts", "ExtractIsoformReadCounts.exe", "", getCaseFiles, getOneOffFiles, getOutputFile, stateOfTheWorld.configuration.nWorkerMachines)
            {
            }

            static GetCaseFile[] getCaseFiles = { _ => _.tumor_rna_allcount_filename, _ => _.normal_rna_file_id == "" ? _.tumor_rna_allcount_filename : _.normal_rna_file_id };
            static GetOneOffFile[] getOneOffFiles = { _ => _.configuration.finalResultsDirectory + ASETools.ASECorrectionFilename };
            static GetCaseFile[] getOutputFile = { _ => _.isoform_read_counts_filename };

        } // ExtractIsoformReadCountsProcessingStage

        class MAFConfigurationProcessingStage : ProcessingStage 
        {
            public MAFConfigurationProcessingStage() { }

            public string GetStageName()
            {
                return "Generate MAF Configuration";
            }

            public bool NeedsCases() { return false; }

            public void EvaluateStage(StateOfTheWorld stateOfTheWorld, StreamWriter script, ASETools.RandomizingStreamWriter hpcScript, StreamWriter linuxScript, StreamWriter azureScript, out List<string> filesToDownload, out int nDone, out int nAddedToScript, out int nWaitingForPrerequisites)
            {
                filesToDownload = null;

                nWaitingForPrerequisites = 0; // This is the very first thing we do, there are never any prerequisites

                if (stateOfTheWorld.mafInfo != null || stateOfTheWorld.configuration.isBeatAML)
                {
                    nDone = 1;
                    nAddedToScript = 0;
                    return;
                }

                script.WriteLine(stateOfTheWorld.configuration.binariesDirectory + "GenerateMAFConfiguration" + stateOfTheWorld.configurationString);

                nDone = 0;
                nAddedToScript = 1;
            }

            public bool EvaluateDependencies(StateOfTheWorld stateOfTheWorld)
            {
                //
                // This is the first stage to run, so there are no dependencies upstream of it.
                //
                return true;
            }
        }

        class GenerateCasesProcessingStage : ProcessingStage
        {
            public GenerateCasesProcessingStage() { }

            public string GetStageName()
            {
                return "Generate Cases";
            }

            public bool NeedsCases() { return false; }

            public void EvaluateStage(StateOfTheWorld stateOfTheWorld, StreamWriter script, ASETools.RandomizingStreamWriter hpcScript, StreamWriter linuxScript, StreamWriter azureScript, out List<string> filesToDownload, out int nDone, out int nAddedToScript, out int nWaitingForPrerequisites)
            {
                filesToDownload = null;

                if (stateOfTheWorld.cases != null)
                {
                    nDone = 1;
                    nAddedToScript = 0;
                    nWaitingForPrerequisites = 0;

                    return;
                }

                nDone = 0;

                if (stateOfTheWorld.mafInfo == null && !stateOfTheWorld.configuration.isBeatAML)
                {
                    nWaitingForPrerequisites = 1;
                    nAddedToScript = 0;

                    return;
                }

                //
                // See if we've downloaded all of the MAFs.
                //
                if (!stateOfTheWorld.configuration.isBeatAML)
                {
                    foreach (var mafEntry in stateOfTheWorld.mafInfo)
                    {
                        if (!stateOfTheWorld.downloadedFiles.ContainsKey(mafEntry.Value.file_id))
                        {
                            if (null == filesToDownload)
                            {
                                filesToDownload = new List<string>();
                            }

                            filesToDownload.Add(mafEntry.Value.file_id);
                        }
                    }
                }

                if (null == filesToDownload)
                {
                    var executable = stateOfTheWorld.configuration.isBeatAML ? "GenerateBeatAMLCases.exe" : "GenerateCases.exe";
                    script.WriteLine(stateOfTheWorld.configuration.binariesDirectory + executable + stateOfTheWorld.configurationString);
                    nAddedToScript = 1;
                    nWaitingForPrerequisites = 0;
                }
                else
                {
                    nWaitingForPrerequisites = 1;
                    nAddedToScript = 0;
                }
            } // EvaluateStage

            public bool EvaluateDependencies(StateOfTheWorld stateOfTheWorld)
            {
                //
                // The cases file is updated every time we run, so it's kind of pointless to check to see if the mafs are newer than it.
                //
                return true;
            }

        } // GenerateCasesProcessingStage

        class AllcountProcesingStage : ProcessingStage
        {
            public AllcountProcesingStage() { }

            public string GetStageName()
            {
                return "Generate Allcount files";
            }

            public bool NeedsCases() { return true; }

            public void EvaluateStage(StateOfTheWorld stateOfTheWorld, StreamWriter script, ASETools.RandomizingStreamWriter hpcScript, StreamWriter linuxScript, StreamWriter azureScript, out List<string> filesToDownload, out int nDone, out int nAddedToScript, out int nWaitingForPrerequisites)
            {
                filesToDownload = null;
                nDone = 0;
                nAddedToScript = 0;
                nWaitingForPrerequisites = 0;
                filesToDownload = new List<string>();

                foreach (var caseEntry in stateOfTheWorld.cases)
                {
                    var case_ = caseEntry.Value;

                    HandleFile(stateOfTheWorld, case_.tumor_rna_file_id, case_.tumor_rna_file_bam_md5, case_.case_id, ASETools.DerivedFile.Type.TumorRNAAllcount,
                        ASETools.tumorRNAAllcountExtension, script, hpcScript, ref filesToDownload, ref nDone, ref nAddedToScript, ref nWaitingForPrerequisites);

                    HandleFile(stateOfTheWorld, case_.normal_dna_file_id, case_.normal_dna_file_bam_md5, case_.case_id, ASETools.DerivedFile.Type.NormalDNAAllcount,
                        ASETools.normalDNAAllcountExtension, script, hpcScript, ref filesToDownload, ref nDone, ref nAddedToScript, ref nWaitingForPrerequisites);

                    HandleFile(stateOfTheWorld, case_.tumor_dna_file_id, case_.tumor_dna_file_bam_md5, case_.case_id, ASETools.DerivedFile.Type.TumorDNAAllcount,
                        ASETools.tumorDNAAllcountExtension, script, hpcScript, ref filesToDownload, ref nDone, ref nAddedToScript, ref nWaitingForPrerequisites);

                    if (case_.normal_rna_file_id != "")
                    {
                        HandleFile(stateOfTheWorld, case_.normal_rna_file_id, case_.normal_rna_file_bam_md5, case_.case_id, ASETools.DerivedFile.Type.NormalRNAAllcount,
                            ASETools.normalRNAAllcountExtension, script, hpcScript, ref filesToDownload, ref nDone, ref nAddedToScript, ref nWaitingForPrerequisites);
                    }

                } // Foreach case
            }// EvaluateStage

            void HandleFile(StateOfTheWorld stateOfTheWorld, string file_id, string expectedMD5, string case_id, ASETools.DerivedFile.Type type, string extension, StreamWriter script, ASETools.RandomizingStreamWriter hpcScript, ref List<string> filesToDownload,ref int nDone, ref int nAddedToScript, ref int nWaitingForPrerequisites)
            {

                if (!stateOfTheWorld.downloadedFiles.ContainsKey(file_id))
                {
                    filesToDownload.Add(file_id);
                }
                else
                {
                    var downloadedFile = stateOfTheWorld.downloadedFiles[file_id];

                    if (!stateOfTheWorld.fileDownloadedAndVerified(file_id, expectedMD5) || (stateOfTheWorld.configuration.isBeatAML && !File.Exists(downloadedFile.fileInfo.FullName.Substring(0, downloadedFile.fileInfo.FullName.Length - 4) + ".bai")))
                    {
                        nWaitingForPrerequisites++;
                    }
                    else if (stateOfTheWorld.containsDerivedFile(case_id, file_id, type))
                    {
                        if (stateOfTheWorld.getDrivedFile(case_id, file_id, type).fileinfo.Length < 200 * 1024)
                        {
                            Console.WriteLine("Suspiciously small allcount file of size " + stateOfTheWorld.getDrivedFile(case_id, file_id, type).fileinfo.Length + ": " + stateOfTheWorld.getDrivedFile(case_id, file_id, type).fileinfo.FullName);
                        }
                        nDone++;
                    }
                    else
                    {
                        nAddedToScript++;
                        string caseDirectory = ASETools.GetDirectoryFromPathname(stateOfTheWorld.downloadedFiles[file_id].fileInfo.FullName) + @"\..\..\" + stateOfTheWorld.configuration.derivedFilesDirectory + @"\" + case_id + @"\";
                        script.WriteLine("md " + caseDirectory + " & " +
                            stateOfTheWorld.configuration.binariesDirectory + "CountReadsCovering " + stateOfTheWorld.configuration.indexDirectory + " -a " + stateOfTheWorld.downloadedFiles[file_id].fileInfo.FullName + " - | " + 
                            stateOfTheWorld.configuration.binariesDirectory + "gzip -9 > " +
                            caseDirectory + file_id + extension);

                        hpcScript.WriteLine(jobAddString + 
                            stateOfTheWorld.configuration.hpcBinariesDirectory + "MakeDirectoryAndCountReadsCovering.cmd " + caseDirectory + " " + stateOfTheWorld.configuration.hpcBinariesDirectory + " " +
                            stateOfTheWorld.configuration.hpcIndexDirectory + " " + stateOfTheWorld.downloadedFiles[file_id].fileInfo.FullName + " " + caseDirectory + file_id + extension);
                    }
                }
            } // HandleFile

            public bool EvaluateDependencies(StateOfTheWorld stateOFTheWorld) 
            {
                if (stateOFTheWorld.cases == null)
                {
                    return true;
                }

                bool allOK = true;
                foreach (var caseEntry in stateOFTheWorld.cases)
                {
                    var case_ = caseEntry.Value;
                    if (!stateOFTheWorld.containsDerivedFile(case_.case_id, case_.tumor_rna_file_id, ASETools.DerivedFile.Type.TumorRNAAllcount))
                    {
                        continue;
                    }

                    if (stateOFTheWorld.derivedFiles[case_.case_id].Where(x => x.type == ASETools.DerivedFile.Type.TumorRNAAllcount).Count() > 1)
                    {
                        Console.Write("More than one tumor RNA allcount file for case " + case_.case_id + ":");
                        foreach (var allcountFile in stateOFTheWorld.derivedFiles[case_.case_id].Where(x => x.type == ASETools.DerivedFile.Type.TumorRNAAllcount))
                        {
                            Console.Write(" " + allcountFile.fileinfo.FullName);
                        }
                        Console.WriteLine();
                        allOK = false;
                    }

                    var singleAllcountFile = stateOFTheWorld.derivedFiles[case_.case_id].Where(x => x.type == ASETools.DerivedFile.Type.TumorRNAAllcount).ToList()[0];

                    if (!stateOFTheWorld.downloadedFiles.ContainsKey(case_.tumor_rna_file_id))
                    {
                        Console.WriteLine("Allcount file " + singleAllcountFile.fileinfo.FullName + " exists, but the BAM from which it was generated does not");
                        allOK = false;
                    }
                }

                return allOK;
            }

        } // AllcountProcessingStage

        class DownloadProcessingStage : ProcessingStage
        {
            public DownloadProcessingStage() { }

            public string GetStageName()
            {
                return "Download";
            }
            public bool NeedsCases() { return true; }

            public void EvaluateStage(StateOfTheWorld stateOfTheWorld, StreamWriter script, ASETools.RandomizingStreamWriter hpcScript, StreamWriter linuxScript, StreamWriter azureScript, out List<string> filesToDownload, out int nDone, out int nAddedToScript, out int nWaitingForPrerequisites)
            {
                filesToDownload = new List<string>();
                nDone = 0;
                nAddedToScript = 0;
                nWaitingForPrerequisites = 0;

                foreach (var caseEntry in stateOfTheWorld.cases)
                {
                    var case_ = caseEntry.Value;

                    var idsToDownload = ASETools.Case.downloadableFileTypes.Select(_ => _.fileIdGetter(case_)).ToList();

                    foreach (var id in idsToDownload) {
                        if (id != null && id != "" && !stateOfTheWorld.downloadedFiles.ContainsKey(id)) {
                            filesToDownload.Add(id);
                        }
                    }

                } // foreach case
            } // EvaluateStage

            public bool EvaluateDependencies(StateOfTheWorld stateOfTheWorld)
            {
                //
                // What we download depends on the cases selected, but we wouldn't re-download no matter what, since the data we get from the sevrer doesn't change.
                //
                return true;
            }
        } // DownloadProcessingStage

        class MD5ComputationProcessingStage : ProcessingStage
        {
            public MD5ComputationProcessingStage() { }

            public string GetStageName()
            {
                return "MD5 Computation";
            }

            public bool NeedsCases() { return true; }

            public void EvaluateStage(StateOfTheWorld stateOfTheWorld, StreamWriter script, ASETools.RandomizingStreamWriter hpcScript, StreamWriter linuxScript, StreamWriter azureScript, out List<string> filesToDownload, out int nDone, out int nAddedToScript, out int nWaitingForPrerequisites)
            {
                filesToDownload = null; // This stage never generates downloads
                nAddedToScript = 0;
                nDone = 0;

                nWaitingForPrerequisites = 0;

                if (!stateOfTheWorld.configuration.downloadedFilesHaveMD5Sums) {
                    return;
                }

                var filenamesChecked = new HashSet<string>();

                foreach (var caseEntry in stateOfTheWorld.cases)
                {
                    var case_ = caseEntry.Value;

#if true
                    foreach (var downloadableFileType in ASETools.Case.downloadableFileTypes)
                    {
                        if (downloadableFileType.md5Getter == null)
                        {
                            HandleFile(stateOfTheWorld, script, hpcScript, downloadableFileType.fileIdGetter(case_), downloadableFileType.bamMD5Getter(case_), ref nDone, ref nAddedToScript, ref nWaitingForPrerequisites, filenamesChecked); // We don't verify BAI
                        }
                        else
                        {
                            HandleFile(stateOfTheWorld, script, hpcScript, downloadableFileType.fileIdGetter(case_), downloadableFileType.md5Getter(case_), ref nDone, ref nAddedToScript, ref nWaitingForPrerequisites, filenamesChecked);
                        }
                    }
#else // true
                    HandleFile(stateOfTheWorld, script, hpcScript, case_.normal_rna_file_id, case_.normal_rna_file_bam_md5, ref nDone, ref nAddedToScript, ref nWaitingForPrerequisites);
                    HandleFile(stateOfTheWorld, script, hpcScript, case_.tumor_rna_file_id, case_.tumor_rna_file_bam_md5, ref nDone, ref nAddedToScript, ref nWaitingForPrerequisites);
                    HandleFile(stateOfTheWorld, script, hpcScript, case_.normal_dna_file_id, case_.normal_dna_file_bam_md5, ref nDone, ref nAddedToScript, ref nWaitingForPrerequisites);
                    HandleFile(stateOfTheWorld, script, hpcScript, case_.tumor_dna_file_id, case_.tumor_dna_file_bam_md5, ref nDone, ref nAddedToScript, ref nWaitingForPrerequisites);
                    HandleFile(stateOfTheWorld, script, hpcScript, case_.tumor_methylation_file_id, case_.tumor_methylation_file_md5, ref nDone, ref nAddedToScript, ref nWaitingForPrerequisites);
                    HandleFile(stateOfTheWorld, script, hpcScript, case_.normal_methylation_file_id, case_.normal_methylation_file_md5, ref nDone, ref nAddedToScript, ref nWaitingForPrerequisites);
                    HandleFile(stateOfTheWorld, script, hpcScript, case_.tumor_copy_number_file_id, case_.tumor_copy_number_file_md5, ref nDone, ref nAddedToScript, ref nWaitingForPrerequisites);
                    HandleFile(stateOfTheWorld, script, hpcScript, case_.normal_copy_number_file_id, case_.normal_copy_number_file_md5, ref nDone, ref nAddedToScript, ref nWaitingForPrerequisites);
                    HandleFile(stateOfTheWorld, script, hpcScript, case_.tumor_fpkm_file_id, case_.tumor_fpkm_file_md5, ref nDone, ref nAddedToScript, ref nWaitingForPrerequisites);
                    HandleFile(stateOfTheWorld, script, hpcScript, case_.normal_fpkm_file_id, case_.normal_fpkm_file_md5, ref nDone, ref nAddedToScript, ref nWaitingForPrerequisites);
                    HandleFile(stateOfTheWorld, script, hpcScript, case_.clinical_supplement_file_id, case_.clinical_supplement_md5, ref nDone, ref nAddedToScript, ref nWaitingForPrerequisites);
                    HandleFile(stateOfTheWorld, script, hpcScript, case_.normal_miRNA_file_id, case_.normal_miRNA_md5, ref nDone, ref nAddedToScript, ref nWaitingForPrerequisites);
                    HandleFile(stateOfTheWorld, script, hpcScript, case_.tumor_miRNA_file_id, case_.tumor_miRNA_md5, ref nDone, ref nAddedToScript, ref nWaitingForPrerequisites);
                    HandleFile(stateOfTheWorld, script, hpcScript, case_.tumor_miRNA_expression_quantification_file_id, case_.tumor_miRNA_expression_quantification_md5, ref nDone, ref nAddedToScript, ref nWaitingForPrerequisites);
                    HandleFile(stateOfTheWorld, script, hpcScript, case_.normal_miRNA_expression_quantification_file_id, case_.normal_miRNA_expression_quantification_md5, ref nDone, ref nAddedToScript, ref nWaitingForPrerequisites);
                    HandleFile(stateOfTheWorld, script, hpcScript, case_.tumor_isoform_expression_quantification_file_id, case_.tumor_isoform_expression_quantification_md5, ref nDone, ref nAddedToScript, ref nWaitingForPrerequisites);
                    HandleFile(stateOfTheWorld, script, hpcScript, case_.normal_isoform_expression_quantification_file_id, case_.normal_isoform_expression_quantification_md5, ref nDone, ref nAddedToScript, ref nWaitingForPrerequisites);
#endif // false

                }
            } // EvaluateStage

            void HandleFile(StateOfTheWorld stateOfTheWorld, StreamWriter script, ASETools.RandomizingStreamWriter hpcScript, string fileId, string expectedMD5, ref int nDone, ref int nAddedToScript, ref int nWaitingForPrerequisites, HashSet<string> filenamesChecked)
            {
                if (fileId == null || fileId == "" || null == expectedMD5 || "" == expectedMD5)
                {
                    //
                    // There is no file at all (not just not downloaded), so it doesn't count in any of the counts.
                    //
                    return;
                }


                if (!stateOfTheWorld.downloadedFiles.ContainsKey(fileId))
                {
                    nWaitingForPrerequisites++;
                    return;
                }

                var downloadedFile = stateOfTheWorld.downloadedFiles[fileId];
                var filename = downloadedFile.fileInfo.FullName.ToLower();

                if (filenamesChecked.Contains(filename))
                {
                    //
                    // A duplicate.  Ignore it.
                    //
                    return;
                }

                filenamesChecked.Add(filename);

                if (filename.EndsWith(".partial"))
                {
                    if (downloadedFile.fileInfo.LastWriteTime < DateTime.Now.AddDays(-1))
                    {
                        Console.WriteLine("Found partial download file that's more than a day old, it's probably abandoned and should be deleted: " + downloadedFile.fileInfo.FullName);
                    }
                    nWaitingForPrerequisites++;
                    return;
                }

                if (downloadedFile.storedMD5 != null && downloadedFile.storedMD5 != "")
                {
                    nDone++;

                    if (downloadedFile.storedMD5 != expectedMD5)
                    {
                        Console.WriteLine("MD5 checksum mismatch on file " + downloadedFile.fileInfo.FullName + " " + downloadedFile.storedMD5 + " != " + expectedMD5);
                    }

                    return;
                }

                script.WriteLine(stateOfTheWorld.configuration.binariesDirectory + "ComputeMD5 " + filename + " > " + filename + ".md5");
                hpcScript.WriteLine(jobAddString + stateOfTheWorld.configuration.hpcBinariesDirectory + "ComputeMD5IntoFile.cmd " +
                    stateOfTheWorld.configuration.hpcBinariesDirectory + " " + filename + " " + filename + ".md5");
                nAddedToScript++;
            }   // HandleFile

            public bool EvaluateDependencies(StateOfTheWorld stateOfTheWorld)
            {
                bool allOk = true;

                foreach (var fileEntry in stateOfTheWorld.downloadedFiles)
                {
                    var downloadedFile = fileEntry.Value;

                    if (downloadedFile.storedMD5 != null && downloadedFile.storedMD5 != "" && downloadedFile.md5FileInfo.LastWriteTime < downloadedFile.fileInfo.LastWriteTime)
                    {
                        Console.WriteLine("Downloaded file " + downloadedFile.fileInfo.FullName + " is newer than its md5 hash.");
                        allOk = false;
                    }
                }

                return allOk;
            } // EvaluateDependencies
        }

        class GermlineVariantCallingProcessingStage : ProcessingStage
        {
            public GermlineVariantCallingProcessingStage() { }

            public string GetStageName()
            {
                return "Germline Variant Calling";
            }

            public bool NeedsCases() { return true; }

            public void EvaluateStage(StateOfTheWorld stateOfTheWorld, StreamWriter script, ASETools.RandomizingStreamWriter hpcScript, StreamWriter linuxScript, StreamWriter azureScript, out List<string> filesToDownload, out int nDone, out int nAddedToScript, out int nWaitingForPrerequisites)
            {
                filesToDownload = new List<string>();
                nDone = 0;
                nAddedToScript = 0;
                nWaitingForPrerequisites = 0;

                foreach (var caseEntry in stateOfTheWorld.cases)
                {
                    var case_ = caseEntry.Value;

                    if (stateOfTheWorld.containsDerivedFile(case_.case_id, case_.normal_dna_file_id, ASETools.DerivedFile.Type.VCF))
                    {
                        nDone++;
                        continue;
                    }


                    //
                    // The Azure script downloads on the fly, so add every one that isn't done.  
                    // NB: we use Write and \n rather than WriteLine to avoid generating crlf text that would confuse Linux
                    //
                    azureScript.Write("date\n");  // NB: we use Write and \n rather than WriteLine to avoid generating crlf text that would confuse Linux
                    if (!stateOfTheWorld.configuration.isBeatAML)
                    { // for BeatAML, we've already manually loaded the files into an Azure files location, and mounted that on /mnt/downloaded_files/
                        azureScript.Write("rm -rf /mnt/downloaded_files/*\n"); // /mnt is a big but temporary filesystem on these azure instances
                        azureScript.Write("sudo mkdir /mnt/downloaded_files/\n");
                        azureScript.Write("sudo chmod 777 /mnt/downloaded_files\n");
                        azureScript.Write("cd /mnt/downloaded_files\n");
                        azureScript.Write(@"~/gdc-client download --token-file ~/" + ASETools.GetFileNameFromPathname(stateOfTheWorld.configuration.accessTokenPathname) + " " + case_.normal_dna_file_id + "\n");
                    }
                    azureScript.Write("cd ~\n");
                    azureScript.Write("rm ~/x\n");    // We use a link from x to /mnt/downloaded_files/<download_directory> to make the command line shorter
                    if (stateOfTheWorld.configuration.isBeatAML)
                    {
                        azureScript.Write("ln -s /mnt/downloaded_files/BeatAML/" + case_.normal_dna_file_id + " ~/x\n");
                    }
                    else
                    {
                        azureScript.Write("ln -s /mnt/downloaded_files/" + case_.normal_dna_file_id + " ~/x\n");
                    }

                    azureScript.Write("cd ~/freebayes/scripts\n");
                    var regionFileName = stateOfTheWorld.configuration.isBeatAML ? "~/genomes/hg19-100k-regions" : "~/genomes/GRCh38.d1.vd1-100k-regions";
                    var fastaName = stateOfTheWorld.configuration.isBeatAML ? "~/genomes/hg19-no-chr.fa" : "~/genomes/GRCh38.d1.vd1.fa";

                    azureScript.Write("./freebayes-parallel " + regionFileName + " `nproc` --fasta-reference " + fastaName + " ~/x/*.bam > ~/" +
                        case_.normal_dna_file_id + ASETools.vcfExtension + "\n");
                    azureScript.Write("if [ $? = 0 ]; then\n");
                    azureScript.Write("    mv ~/" + case_.normal_dna_file_id + ASETools.vcfExtension + " ~/completed_vcfs/\n");
                    azureScript.Write("else\n");
                    azureScript.Write("    echo " + case_.normal_dna_file_id + " >> ~/variant_calling_errors\n");
                    azureScript.Write("fi\n");
                    azureScript.Write("cd ~\n");
                    azureScript.Write("rm ~/" + case_.normal_dna_file_id + ASETools.vcfExtension + "\n");
                    if (!stateOfTheWorld.configuration.isBeatAML) // Don't delete it for BeatAML, since we hand uploaded it.
                    {
                        azureScript.Write("rm -rf ~/downloaded_files/" + case_.normal_dna_file_id + "\n");
                    }
                    azureScript.Write("rm ~/x\n");

                    if (!stateOfTheWorld.fileDownloadedAndVerified(case_.normal_dna_file_id, case_.normal_dna_file_bam_md5))
                    {
                        nWaitingForPrerequisites++;
                        continue;
                    }

                    linuxScript.Write("date\n");

                    var mountpoint = "/mnt/" + ASETools.ComputerFromPathname(case_.normal_dna_filename);
                    linuxScript.Write("sudo mkdir " + mountpoint + "\n");
                    linuxScript.Write("sudo chmod 777 " + mountpoint + "\n");
                    linuxScript.Write("sudo mount -t drvfs '" + ASETools.ShareFromPathname(case_.normal_dna_filename) + "' " + mountpoint + "\n");
                    var copiedBamDirectory = "~/" + case_.normal_dna_file_id;
                    linuxScript.Write("mkdir " + copiedBamDirectory + "\n");
                    linuxScript.Write("cp " + mountpoint + ASETools.PathnameToLinuxPathname(ASETools.PathnameWithoutUNC(case_.normal_dna_filename)) + " " + copiedBamDirectory + "/\n");
                    linuxScript.Write("cp " + mountpoint + ASETools.PathnameToLinuxPathname(ASETools.PathnameWithoutUNC(ASETools.GetDirectoryFromPathname(case_.normal_dna_filename))) + "/*bai " + copiedBamDirectory + "/\n");

                    linuxScript.Write("cd ~/freebayes/scripts\n");
                    var outputFilename = case_.normal_dna_file_id + ASETools.vcfExtension;
                    linuxScript.Write("./freebayes-parallel " + regionFileName + " `nproc` --fasta-reference " + fastaName + " " + copiedBamDirectory + "/" + ASETools.GetFileNameFromPathname(case_.normal_dna_filename) +
                        " > ~/" + outputFilename + "\n");

                    linuxScript.Write("if [ $? = 0 ]; then\n");
                    var outputDirectory = mountpoint + ASETools.PathnameToLinuxPathname(ASETools.PathnameWithoutUNC(ASETools.GetDirectoryFromPathname(case_.normal_dna_filename)) + @"\..\..\" + stateOfTheWorld.configuration.derivedFilesDirectory + @"\" + case_.case_id);

                    linuxScript.Write("    mkdir " + outputDirectory + "\n");
                    linuxScript.Write("    cp ~/" + outputFilename + " " + outputDirectory + "/\n");
                    linuxScript.Write("    rm ~/" + outputFilename + "\n");
                    linuxScript.Write("else\n");
                    linuxScript.Write(@"    echo " + case_.normal_dna_file_id + " >> variant_calling_errors\n");
                    linuxScript.Write("fi\n");
                    linuxScript.Write("sleep 60\n");    // Time to let the copy finish before we can umount
                    linuxScript.Write("sudo umount " + mountpoint + "\n");
                    linuxScript.Write("sudo rmdir " + mountpoint + "\n");
                    linuxScript.Write("rm -rf " + copiedBamDirectory + "\n"); // * is to get bai as well

                    nAddedToScript++;
                } // foreach case
            } // EvaluateStage

            public bool EvaluateDependencies(StateOfTheWorld stateOfTheWorld)
            {
                //
                // Skip this becuase we ran the variant calling on Azure, which downloaded the normal DNA files and then deleted them, 
                // so the local download of normal DNA may well be after the VCF was created.
                //

                return true;
            } // EvaluateDependencies

        }  // GermlineVariantCallingProcessingStage

        class AnnotateVariantsProcessingStage : PerCaseProcessingStage
        {
            public AnnotateVariantsProcessingStage(StateOfTheWorld stateOfTheWorld) : base("Annotate Variants", "AnnotateVariants.exe", "", getCaseFile, null, getOutputFile, stateOfTheWorld.configuration.nWorkerMachines)
            { }

            static GetCaseFile[] getCaseFile =
            {
                _ => _.extracted_maf_lines_filename,
                _ => _.tentative_selected_variants_filename,
                _ => _.tumor_dna_reads_at_tentative_selected_variants_filename,
                _ => _.tumor_dna_reads_at_tentative_selected_variants_index_filename,
                _ => _.tumor_rna_reads_at_tentative_selected_variants_filename,
                _ => _.tumor_rna_reads_at_tentative_selected_variants_index_filename,
                _ => _.normal_dna_reads_at_tentative_selected_variants_filename,
                _ => _.normal_dna_reads_at_tentative_selected_variants_index_filename,
                _ => (_.normal_rna_file_id != "") ? _.normal_rna_reads_at_tentative_selected_variants_filename : _.tumor_rna_reads_at_tentative_selected_variants_filename, // we have to supply a file, so if we don't need it we just reuse another one
                _ => (_.normal_rna_file_id != "") ? _.normal_rna_reads_at_tentative_selected_variants_index_filename : _.tumor_rna_reads_at_tentative_selected_variants_index_filename, // we have to supply a file, so if we don't need it we just reuse another one
            };

            static GetCaseFile[] getOutputFile =
            {
                _ => _.tentative_annotated_selected_variants_filename
            };
        } // AnnotateVariantsProcessingStage

#if false

        class AnnotateVariantsProcessingStage : ProcessingStage
		{
			public AnnotateVariantsProcessingStage() {}

			public string GetStageName() { return "Annotate Variants"; }

			public bool NeedsCases() { return true; }

			public void EvaluateStage(StateOfTheWorld stateOfTheWorld, StreamWriter script, ASETools.RandomizingStreamWriter hpcScript, StreamWriter linuxScript, StreamWriter azureScript, out List<string> filesToDownload, out int nDone, out int nAddedToScript, out int nWaitingForPrerequisites)
			{
				nDone = 0;
				nAddedToScript = 0;
				nWaitingForPrerequisites = 0;
				filesToDownload = null;

                string casesToProcess = "";
                int nCasesToProcess = 0;

				foreach (var caseEntry in stateOfTheWorld.cases)
				{
					var case_ = caseEntry.Value;
					if (case_.tentative_annotated_selected_variants_filename != "")
					{
						nDone++;
						continue;
					}
					else if (case_.extracted_maf_lines_filename == "" || case_.tentative_selected_variants_filename == "" || case_.tumor_dna_reads_at_tentative_selected_variants_filename == "" || case_.tumor_dna_reads_at_tentative_selected_variants_index_filename == "" ||
					case_.tumor_rna_reads_at_tentative_selected_variants_filename == "" || case_.tumor_rna_reads_at_tentative_selected_variants_index_filename == "" || case_.normal_dna_reads_at_tentative_selected_variants_filename == "" || case_.normal_dna_reads_at_tentative_selected_variants_index_filename == "" ||
                    (case_.normal_rna_file_id != "" && (case_.normal_rna_reads_at_tentative_selected_variants_filename == "" || case_.normal_rna_reads_at_tentative_selected_variants_index_filename == "")))
					{
						nWaitingForPrerequisites++;
						continue;
					}
					else
					{
						nAddedToScript++;
					}

                    casesToProcess += case_.case_id + " ";  // Batch them both to allow for thread paralleleism within the program and also to reduce the number of (slow) job add commands to set up the script.
                    nCasesToProcess++;

                    if (nCasesToProcess >= 80)
                    {
                        AddCasesToScripts(stateOfTheWorld, casesToProcess, script, hpcScript);
                        casesToProcess = "";
                        nCasesToProcess = 0;
                    }
				} // foreach case

                if (casesToProcess != "")
                {
                    AddCasesToScripts(stateOfTheWorld, casesToProcess, script, hpcScript);
                }
			} // EvaluateStage

            void AddCasesToScripts(StateOfTheWorld stateOfTheWorld, string casesToProcess, StreamWriter script, ASETools.RandomizingStreamWriter hpcScript)
            {
                script.WriteLine(stateOfTheWorld.configuration.binariesDirectory + "AnnotateVariants.exe " + stateOfTheWorld.configurationString + casesToProcess);
                hpcScript.WriteLine(jobAddString + stateOfTheWorld.configuration.hpcBinariesDirectory + "AnnotateVariants.exe " + stateOfTheWorld.configurationString + casesToProcess);

            }

			public bool EvaluateDependencies(StateOfTheWorld stateOfTheWorld)
			{
				bool allOK = true;

				foreach (var caseEntry in stateOfTheWorld.cases)
				{
					var case_ = caseEntry.Value;

					if (case_.extracted_maf_lines_filename == "" || case_.tentative_selected_variants_filename == "" || case_.tumor_dna_reads_at_tentative_selected_variants_filename == "" || case_.tumor_dna_reads_at_tentative_selected_variants_index_filename == "" ||
					case_.tumor_rna_reads_at_tentative_selected_variants_filename == "" || case_.tumor_rna_reads_at_tentative_selected_variants_index_filename == "" || case_.normal_dna_reads_at_tentative_selected_variants_filename == "" || case_.normal_dna_reads_at_tentative_selected_variants_index_filename == "")
					{
						Console.WriteLine("Annotated variants file " + case_.annotated_selected_variants_filename + " exists, but dependencies do not.");
						allOK = false;
						continue;
					}

					if (case_.annotated_selected_variants_filename == "")
					{
						continue;
					}

					var annotatedVariantsWriteTime = new FileInfo(case_.annotated_selected_variants_filename).LastWriteTime;
					if (case_.annotated_selected_variants_filename == "")
					{
						Console.WriteLine("Annotated variants file " + case_.annotated_selected_variants_filename + " exists, but the precursor annotated selected variants file does not.");
						allOK = false;
						continue;
					}
				}
				return allOK;
			} // EvaluateDependencies

		} // AnnotateVariantsProcessingStage
#endif

#if false // This one isn't used, and this version isn't even completely written.
        class MethylationProcessingStage : ProcessingStage
		{

			public MethylationProcessingStage() { }

			public string GetStageName() { return "Methylation"; }

			public bool NeedsCases() { return true; }

			public void EvaluateStage(StateOfTheWorld stateOfTheWorld, StreamWriter script, ASETools.RandomizingStreamWriter hpcScript, StreamWriter linuxScript, StreamWriter azureScript, out List<string> filesToDownload, out int nDone, out int nAddedToScript, out int nWaitingForPrerequisites)
			{
				nDone = 0;
				nAddedToScript = 0;
				nWaitingForPrerequisites = 0;
				filesToDownload = null;

				foreach (var caseEntry in stateOfTheWorld.cases)
				{
					var case_ = caseEntry.Value;
					if (case_.extracted_maf_lines_filename == "" || case_.tumor_methylation_filename == "")
					{
						nWaitingForPrerequisites++;
						continue;
					}
					else
					{
						nAddedToScript++;
					}

					script.WriteLine(stateOfTheWorld.configuration.binariesDirectory + "MethylationAnalysis.exe 1000000");
					hpcScript.WriteLine(jobAddString + stateOfTheWorld.configuration.hpcBinariesDirectory + "MethylationAnalysis.exe 1000000");
				}


			} // EvaluateStage

			public bool EvaluateDependencies(StateOfTheWorld stateOfTheWorld)
			{
				bool allOK = true;

				foreach (var caseEntry in stateOfTheWorld.cases)
				{
					var case_ = caseEntry.Value;

					if (case_.extracted_maf_lines_filename == "" || case_.tumor_methylation_filename == "")
					{
						Console.WriteLine("Regional methylation file " + case_.annotated_selected_variants_filename + " exists, but dependencies do not.");
						allOK = false;
						continue;
					}
				}
				return allOK;
			} // EvaluateDependencies

		} // MethylationProcessingStage
#endif

#if false
        class SelectVariantsProcessingStage : ProcessingStage
        {
            public SelectVariantsProcessingStage() { }

            public string GetStageName()
            {
                return "Select Tentative Germline Variants";
            }

            public bool NeedsCases() { return true; }

            public void EvaluateStage(StateOfTheWorld stateOfTheWorld, StreamWriter script, ASETools.RandomizingStreamWriter hpcScript, StreamWriter linuxScript, StreamWriter azureScript, out List<string> filesToDownload, out int nDone, out int nAddedToScript, out int nWaitingForPrerequisites)
            {
                nDone = 0;
                nAddedToScript = 0;
                filesToDownload = null;
                nWaitingForPrerequisites = 0;

                int nOnCurrentLine = 0;

                if (!File.Exists(stateOfTheWorld.configuration.redundantChromosomeRegionFilename) || !File.Exists(stateOfTheWorld.configuration.finalResultsDirectory + ASETools.PerGeneASEMapFilename))
                {
                    nWaitingForPrerequisites = 1;
                    return;
                }

                foreach (var caseEntry in stateOfTheWorld.cases)
                {
                    var case_ = caseEntry.Value;

                    if (case_.tentative_selected_variants_filename != "")
                    {
                        nDone++;
                        continue;
                    }

                    if (case_.vcf_filename == "" || case_.tumor_rna_allcount_filename == "" || case_.tumor_dna_allcount_filename == "" ||
                        case_.all_maf_lines_filename == "")
                    {
                        nWaitingForPrerequisites++;
                        continue;
                    }

                    nAddedToScript++;

                    if (nOnCurrentLine >= 100)
                    {
                        script.WriteLine();
                        hpcScript.WriteLine();
                        nOnCurrentLine = 0;
                    }

                    if (nOnCurrentLine == 0) 
                    {
                        script.Write(stateOfTheWorld.configuration.binariesDirectory + "SelectGermlineVariants.exe" + stateOfTheWorld.configurationString);
                        hpcScript.Write(jobAddString + stateOfTheWorld.configuration.hpcBinariesDirectory + "SelectGermlineVariants.exe" + stateOfTheWorld.configurationString);
                    }

                    script.Write(" " + case_.case_id);
                    hpcScript.Write(" " + case_.case_id);

                    nOnCurrentLine++;

                } // foreach case

                if (nOnCurrentLine > 0)
                {
                    script.WriteLine();
                    hpcScript.WriteLine();
                }
            } // EvaluateStage

            public bool EvaluateDependencies(StateOfTheWorld stateOfTheWorld)
            {
                bool allOK = true;

                foreach (var caseEntry in stateOfTheWorld.cases)
                {
                    var case_ = caseEntry.Value;

                    if (!stateOfTheWorld.containsDerivedFile(case_.case_id, case_.normal_dna_file_id, ASETools.DerivedFile.Type.SelectedVariants))
                    {
                        continue;
                    }

                    if (case_.vcf_filename == "" || case_.tumor_rna_allcount_filename == "" || case_.tumor_dna_allcount_filename == "")
                    {
                        Console.WriteLine(case_.tentative_selected_variants_filename + " depends on a file that is missing.");
                        allOK = false;
                        continue;
                    }

                    var tentativeSelectedVariantsWriteTime = new FileInfo(case_.tentative_selected_variants_filename).LastWriteTime;
                    allOK &= checkOneDependency(case_.tentative_selected_variants_filename, tentativeSelectedVariantsWriteTime, case_.vcf_filename);
                    allOK &= checkOneDependency(case_.tentative_selected_variants_filename, tentativeSelectedVariantsWriteTime, case_.tumor_dna_allcount_filename);
                    allOK &= checkOneDependency(case_.tentative_selected_variants_filename, tentativeSelectedVariantsWriteTime, case_.tumor_rna_allcount_filename);
                }

                return allOK;
            } // EvaluateDependencies

            bool checkOneDependency(string selectedVariantsFilename, DateTime selectedVariantsLastWriteTime, string sourceFilename)
            {
                if (selectedVariantsLastWriteTime < new FileInfo(sourceFilename).LastWriteTime)
                {
                    Console.WriteLine(selectedVariantsFilename + " is older than " + sourceFilename + ", upon which it depends.");
                    return false;
                }

                return true;
            } // checkOneDependency
        } // SelectVariantsProcessingStage
#else
        class SelectVariantsProcessingStage : PerCaseProcessingStage
        {
            public SelectVariantsProcessingStage() : base ("Select Tentative Germline Variants", "SelectGermlineVariants.exe", "", getCaseFiles, getOneOffFiles, getOutputFiles, 0, 100)
            { }

            static GetCaseFile[] getCaseFiles = { _ => _.vcf_filename, _ => _.tumor_rna_allcount_filename, _ => _.tumor_dna_allcount_filename, _ => _.all_maf_lines_filename };
            static GetOneOffFile[] getOneOffFiles = { _ => _.configuration.redundantChromosomeRegionFilename};
            static GetCaseFile[] getOutputFiles = { _ => _.tentative_selected_variants_filename };
        } // SelectVariantsProcessingStage
#endif

#if true
        class ExpressionDistributionProcessingStage : PerDiseaseProcessingStage
        {
            public ExpressionDistributionProcessingStage(StateOfTheWorld stateOfTheWorld) : base("Per-disease mRNA expression distribution", "ExpressionDistribution.exe", false,
                stateOfTheWorld.configuration.casesFilePathname + " " + stateOfTheWorld.configuration.expressionFilesDirectory + " " + ASETools.Case.ProjectColumn + " " + 
                ASETools.Case.TumorRNAAllcountFilenameColumn + " " + ASETools.Case.TumorRNAMappedBaseCountColumn, getPerCaseInputs, null, null, getOutputs)
            { }

            static GetCaseFile[] getPerCaseInputs = { _ => _.tumor_rna_allcount_filename, _ => _.tumor_rna_mapped_base_count_filename };
            static GetPerDiseaseFile[] getOutputs = { (stateOfTheWorld, disease) => stateOfTheWorld.configuration.expressionFilesDirectory + ASETools.Expression_filename_base + disease };
        }
#else
        class ExpressionDistributionProcessingStage : ProcessingStage
        {
            public ExpressionDistributionProcessingStage() { }

            public string GetStageName()
            {
                return "Per-disease mRNA expression distribution";
            }

            public bool NeedsCases() { return true; }

            public void EvaluateStage(StateOfTheWorld stateOfTheWorld, StreamWriter script, ASETools.RandomizingStreamWriter hpcScript, StreamWriter linuxScript, StreamWriter azureScript, out List<string> filesToDownload, out int nDone, out int nAddedToScript, out int nWaitingForPrerequisites)
            {
                nDone = 0;
                nAddedToScript = 0;
                nWaitingForPrerequisites = 0;
                filesToDownload = null;

                foreach (var disease in stateOfTheWorld.diseases)
                {
                    if (stateOfTheWorld.expressionFiles.ContainsKey(disease)) {
                        nDone++;
                    } else {
                        bool missingAny = false;
                        foreach (var caseEntry in stateOfTheWorld.cases.Where(x => x.Value.disease() == disease))
                        {
                            var case_ = caseEntry.Value;

                            if (!stateOfTheWorld.containsDerivedFile(case_.case_id, case_.tumor_rna_file_id, ASETools.DerivedFile.Type.TumorRNAAllcount) || 
                                !stateOfTheWorld.containsDerivedFile(case_.case_id, case_.tumor_rna_file_id, ASETools.DerivedFile.Type.TumorRNAMappedBaseCount))
                            {
                                nWaitingForPrerequisites++;
                                missingAny = true;
                                break;
                            }
                        }

                        if (missingAny)
                        {
                            continue;
                        }

                        string command = "ExpressionDistribution.exe " + stateOfTheWorld.configuration.casesFilePathname + " " +
                            stateOfTheWorld.configuration.expressionFilesDirectory + " " + ASETools.Case.ProjectColumn + " " + ASETools.Case.TumorRNAAllcountFilenameColumn + " " + ASETools.Case.TumorRNAMappedBaseCountColumn + " " + disease;

                        script.WriteLine(stateOfTheWorld.configuration.binariesDirectory + command);

                        hpcScript.WriteLine(jobAddString + stateOfTheWorld.configuration.hpcBinariesDirectory + command);
                        nAddedToScript++;
                    }
                } // foreach disease
            } // EvaluateStage

            public bool EvaluateDependencies(StateOfTheWorld stateOfTheWorld)
            {
                bool worked = true;

                foreach (var disease in stateOfTheWorld.diseases)
                {
                    if (!stateOfTheWorld.expressionFiles.ContainsKey(disease))
                    {
                        continue;
                    }

                    var expressionFileLastWriteTime = stateOfTheWorld.expressionFiles[disease].LastWriteTime;

                    foreach (var caseEntry in stateOfTheWorld.cases.Where(x => x.Value.disease() == disease))
                    {
                        var case_ = caseEntry.Value;

                        if (!stateOfTheWorld.containsDerivedFile(case_.case_id, case_.tumor_rna_file_id, ASETools.DerivedFile.Type.TumorRNAAllcount) ||
                            !stateOfTheWorld.containsDerivedFile(case_.case_id, case_.tumor_rna_file_id, ASETools.DerivedFile.Type.TumorRNAMappedBaseCount))
                        {
                            Console.WriteLine("Expression file " + stateOfTheWorld.expressionFiles[disease].FullName + " exists, but a prerequisite from case " + case_.case_id + " does not.");
                            worked = false;
                            break;   // Out of cases for this disease; keep checking other diseases
                        }

                        if (stateOfTheWorld.getDrivedFile(case_.case_id, case_.tumor_rna_file_id, ASETools.DerivedFile.Type.TumorRNAMappedBaseCount).fileinfo.LastWriteTime > expressionFileLastWriteTime)
                        {
                            Console.WriteLine("Expression file " + stateOfTheWorld.expressionFiles[disease].FullName + " is older than " +
                                stateOfTheWorld.getDrivedFile(case_.case_id, case_.tumor_rna_file_id, ASETools.DerivedFile.Type.TumorRNAMappedBaseCount).fileinfo.FullName + ", upon which it depends.");
                            worked = false;
                            break;  // Out of cases for this disease; keep checking other diseases
                        }

                        if (stateOfTheWorld.getDrivedFile(case_.case_id, case_.tumor_rna_file_id, ASETools.DerivedFile.Type.TumorRNAAllcount).fileinfo.LastWriteTime > expressionFileLastWriteTime)
                        {
                            Console.WriteLine("Expression file " + stateOfTheWorld.expressionFiles[disease].FullName + " is older than " +
                                stateOfTheWorld.getDrivedFile(case_.case_id, case_.tumor_rna_file_id, ASETools.DerivedFile.Type.TumorRNAAllcount).fileinfo.FullName + ", upon which it depends.");
                            worked = false;
                            break;  // Out of cases for this disease; keep checking other diseases
                        }
                    } // Foreach case
                } // foreach disease

                return worked;
            } // EvaluateDependencies
        } // ExpressionDistributionProcessingStage
#endif

        class ExtractMAFLinesProcessingStage : ProcessingStage
        {
            public string GetStageName() { return "Extract MAF Lines"; }

            public bool NeedsCases() { return true; }

            public void EvaluateStage(StateOfTheWorld stateOfTheWorld, StreamWriter script, ASETools.RandomizingStreamWriter hpcScript, StreamWriter linuxScript, StreamWriter azureScript, out List<string> filesToDownload, out int nDone, out int nAddedToScript, out int nWaitingForPrerequisites)
            {
                filesToDownload = null;

                nWaitingForPrerequisites = 0;
                nDone = 0;
                nAddedToScript = 0;

                foreach (var caseEntry in stateOfTheWorld.cases)
                {
                    var case_ = caseEntry.Value;

                    if (case_.maf_filename == null || case_.maf_filename == "")
                    {
                        nWaitingForPrerequisites++;
                    }
                    else if (case_.extracted_maf_lines_filename != "" && case_.all_maf_lines_filename != "")
                    {
                        nDone++;
                    }
                    else
                    {
                        nAddedToScript++;
                    }
                }

                if (nAddedToScript > 0)
                {
                    script.WriteLine(stateOfTheWorld.configuration.binariesDirectory + "ExtractMAFLines" + stateOfTheWorld.configurationString);
                    hpcScript.WriteLine(jobAddString + stateOfTheWorld.configuration.hpcBinariesDirectory + "ExtractMAFLines" + stateOfTheWorld.configurationString);
                }
            }

            public bool EvaluateDependencies(StateOfTheWorld stateOfTheWorld)
            {
                if (stateOfTheWorld.cases == null)
                {
                    return true;
                }

                bool allOK = true;
                foreach (var caseEntry in stateOfTheWorld.cases)
                {
                    var case_ = caseEntry.Value;

                    if (stateOfTheWorld.containsDerivedFile(case_.case_id, case_.case_id, ASETools.DerivedFile.Type.ExtractedMAFLines))
                    {
                        var derivedFile = stateOfTheWorld.derivedFiles[case_.case_id].Where(x => x.type == ASETools.DerivedFile.Type.ExtractedMAFLines).ToList()[0];

                        if (!stateOfTheWorld.downloadedFiles.ContainsKey(case_.maf_file_id)) {
                            Console.WriteLine("Case " + case_.case_id + " contains an extracted MAF lines file (" + derivedFile.fileinfo.FullName + "), but the corresponding MAF doesn't exist.");
                            allOK = false;
                            continue;
                        }

                        if (derivedFile.fileinfo.LastWriteTime < stateOfTheWorld.downloadedFiles[case_.maf_file_id].fileInfo.LastWriteTime) 
                        {
                            Console.WriteLine("Extracted MAF Lines file " + derivedFile.fileinfo.FullName + " is older than the MAF from which it's derived (" + stateOfTheWorld.downloadedFiles[case_.maf_file_id].fileInfo.FullName + ")");
                            allOK = false;
                        }
                    } // if the case has an extracted MAF Lines file
                } // foreach case

                return allOK;
            } // EvaluateDependencies

        } // ExtractMAFLinesProcessingStage

        class RegionalExpressionProcessingStage : ProcessingStage
        {
            public RegionalExpressionProcessingStage() { }

            public string GetStageName() { return "Regional Expression"; }

            public bool NeedsCases() { return true; }

            public void EvaluateStage(StateOfTheWorld stateOfTheWorld, StreamWriter script, ASETools.RandomizingStreamWriter hpcScript, StreamWriter linuxScript, StreamWriter azureScript, out List<string> filesToDownload, out int nDone, out int nAddedToScript, out int nWaitingForPrerequisites)
            {
                nDone = 0;
                nAddedToScript = 0;
                nWaitingForPrerequisites = 0;
                filesToDownload = null;

                var casesReadyToGoByDisease = new Dictionary<string, List<ASETools.Case>>();
                const int maxCasesPerCommandLine = 200;

                foreach (var caseEntry in stateOfTheWorld.cases)
                {
                    var case_ = caseEntry.Value;
                    if (case_.regional_expression_filename != "")
                    {
                        nDone++;
                    }
                    else if (!stateOfTheWorld.expressionFiles.ContainsKey(case_.disease()) || case_.tumor_rna_allcount_filename == "")
                    {
                        nWaitingForPrerequisites++;
                    }
                    else
                    {
                        nAddedToScript++;

                        if (!casesReadyToGoByDisease.ContainsKey(case_.disease()))
                        {
                            casesReadyToGoByDisease.Add(case_.disease(), new List<ASETools.Case>());
                        }

                        casesReadyToGoByDisease[case_.disease()].Add(case_);

                        if (casesReadyToGoByDisease[case_.disease()].Count() >= maxCasesPerCommandLine)
                        {
                            WriteScripts(stateOfTheWorld, casesReadyToGoByDisease[case_.disease()], script, hpcScript);
                            casesReadyToGoByDisease[case_.disease()] = new List<ASETools.Case>();
                        }
                    }
                } // foreach case

                foreach (var diseaseEntry in casesReadyToGoByDisease)
                {
                    if (diseaseEntry.Value.Count() > 0)
                    {
                        WriteScripts(stateOfTheWorld, diseaseEntry.Value, script, hpcScript);
                    }
                }
            } // EvaluateStage

            void WriteScripts(StateOfTheWorld stateOfTheWorld, List<ASETools.Case> cases, StreamWriter script, ASETools.RandomizingStreamWriter hpcScript)
            {
                script.Write(stateOfTheWorld.configuration.binariesDirectory + "RegionalExpression " + stateOfTheWorld.configurationString + stateOfTheWorld.expressionFiles[cases[0].disease()].FullName + " " + stateOfTheWorld.configuration.regionalExpressionRegionSize + " ");
                hpcScript.Write(jobAddString + stateOfTheWorld.configuration.hpcBinariesDirectory + "RegionalExpression " + stateOfTheWorld.configurationString + stateOfTheWorld.expressionFiles[cases[0].disease()].FullName + " " + stateOfTheWorld.configuration.regionalExpressionRegionSize + " ");
                foreach (var case_ in cases) {
                    script.Write(" " + case_.case_id);
                    hpcScript.Write(" " + case_.case_id);
                }
                script.WriteLine();
                hpcScript.WriteLine();
            } // WriteScripts

            public bool EvaluateDependencies(StateOfTheWorld stateOfTheWorld)
            {
                bool allOK = true;

                foreach (var caseEntry in stateOfTheWorld.cases)
                {
                    var case_ = caseEntry.Value;

                    if (case_.regional_expression_filename == "")
                    {
                        continue;
                    }

                    if (!stateOfTheWorld.expressionFiles.ContainsKey(case_.disease()))
                    {
                        Console.WriteLine("Missing per-disease expression file for " + case_.disease() + " even though regional expression file " + case_.regional_expression_filename + " exists.");
                        allOK = false;
                        continue;
                    }

                    var regionalExpressionWriteTime = new FileInfo(case_.regional_expression_filename).LastWriteTime;
                    if (stateOfTheWorld.expressionFiles[case_.disease()].LastWriteTime > regionalExpressionWriteTime)
                    {
                        Console.WriteLine("Regional expression file " + case_.regional_expression_filename + " is newer than the expression_ file on which it depends.");
                        allOK = false;
                        continue;
                    }

                    if (case_.tumor_rna_allcount_filename == "")
                    {
                        Console.WriteLine("Regional expression file " + case_.regional_expression_filename + " exists, but the precursor tumor rna allcount file does not.");
                        allOK = false;
                        continue;
                    }

                    if (new FileInfo(case_.tumor_rna_allcount_filename).LastWriteTime > regionalExpressionWriteTime)
                    {
                        Console.WriteLine("Regional expression file " + case_.regional_expression_filename + " is older than its tumor rna allcount file " + case_.tumor_rna_allcount_filename);
                        allOK = false;
                        continue;
                    }
                }

                return allOK;
            } // EvaluateDependencies

        } // RegionalExpressionProcessingStage

#if false
        class ExpressionNearMutationsProcessingStage : ProcessingStage
        {
			bool forAlleleSpecificExpression;

            public ExpressionNearMutationsProcessingStage(bool forAlleleSpecificExpression_) {
				forAlleleSpecificExpression = forAlleleSpecificExpression_;
			}

            public string GetStageName() {
                if (forAlleleSpecificExpression)
                {
                    return "Allele-Specific Expresssion Near Mutations";
                }
                return "Expresssion Near Mutations";
            }

            public bool NeedsCases() { return true; }

            public void EvaluateStage(StateOfTheWorld stateOfTheWorld, StreamWriter script, ASETools.RandomizingStreamWriter hpcScript, StreamWriter linuxScript, StreamWriter azureScript, out List<string> filesToDownload, out int nDone, out int nAddedToScript, out int nWaitingForPrerequisites)
            {
                nDone = 0;
                nAddedToScript = 0;
                nWaitingForPrerequisites = 0;
                filesToDownload = null;

                var currentCommandLine = "";

                if (forAlleleSpecificExpression && 
                    (!File.Exists(stateOfTheWorld.configuration.finalResultsDirectory + ASETools.ASECorrectionFilename) ||
                    !File.Exists(stateOfTheWorld.configuration.finalResultsDirectory + ASETools.PerGeneASEMapFilename)))
                {
                    nWaitingForPrerequisites++;
                    return;
                }

                foreach (var caseEntry in stateOfTheWorld.cases)
                {
                    var case_ = caseEntry.Value;
					if (forAlleleSpecificExpression)
					{
						if (case_.tumor_allele_specific_gene_expression_filename != "")
						{
							if (case_.normal_rna_filename == "")
							{
								nDone++;
								continue;
							}
							else // if normal rna exists and normal ASE was calculated
							{
								if (case_.normal_allele_specific_gene_expression_filename != "")
								{
									nDone++;
									continue;
								}
							}
						}
						else if (case_.maf_filename == "" || case_.annotated_selected_variants_filename == "" || (case_.tumor_copy_number_filename == "" && !stateOfTheWorld.configuration.isBeatAML) || 
                            case_.extracted_maf_lines_filename == "")
						{
							nWaitingForPrerequisites++;
							continue;
						}
						else
						{
							nAddedToScript++;
						}
					}
					else
					{
						if (case_.gene_expression_filename != "")
						{
							nDone++;
							continue;
						}
						else if (case_.maf_filename == "" || case_.regional_expression_filename == ""  /* unfiltered counts, which we don't have a place for yet */)
						{
							nWaitingForPrerequisites++;
							continue;
						}
						else
						{
							nAddedToScript++;
						}
					}

                    if (currentCommandLine == "")
                    {
                        currentCommandLine = "ExpressionNearMutations.exe" + stateOfTheWorld.configurationString + (forAlleleSpecificExpression ? " -a" : "");
                    }

                    currentCommandLine += " " + case_.case_id;

                    if (currentCommandLine.Count() > 5000)
                    {
                        script.WriteLine(stateOfTheWorld.configuration.binariesDirectory + currentCommandLine);
                        hpcScript.WriteLine(jobAddString + stateOfTheWorld.configuration.hpcBinariesDirectory + currentCommandLine);

                        currentCommandLine = "";
                    }

				}

                if (currentCommandLine != "")
                {
                    script.WriteLine(stateOfTheWorld.configuration.binariesDirectory + currentCommandLine);
                    hpcScript.WriteLine(jobAddString + stateOfTheWorld.configuration.hpcBinariesDirectory + currentCommandLine);
                }


            } // EvaluateStage

            public bool EvaluateDependencies(StateOfTheWorld stateOfTheWorld)
            {
				bool allOK = true;

				foreach (var caseEntry in stateOfTheWorld.cases)
				{
					var case_ = caseEntry.Value;

					if (case_.maf_filename == "")
					{
						Console.WriteLine("Gene expression file " + case_.gene_expression_filename + " exists, but the MAF file does not.");
						allOK = false;
						continue;
					}

					if (forAlleleSpecificExpression)
					{
						if (case_.tumor_allele_specific_gene_expression_filename == "" && case_.normal_allele_specific_gene_expression_filename == "")
						{
							continue;
						}

						if (case_.annotated_selected_variants_filename == "")
						{
							Console.WriteLine("AS gene expression file " + case_.tumor_allele_specific_gene_expression_filename + " exists, but the precursor annotated selected variants file does not.");
							allOK = false;
							continue;
						}

						var asGeneExpressionWriteTime = new FileInfo(case_.tumor_allele_specific_gene_expression_filename).LastWriteTime;
						if (new FileInfo(case_.annotated_selected_variants_filename).LastWriteTime > asGeneExpressionWriteTime)
						{
							Console.WriteLine("AS gene expression tumor file " + case_.tumor_allele_specific_gene_expression_filename + " is older than its regional file " + case_.regional_expression_filename);
							allOK = false;
							continue;
						}
						if (case_.normal_allele_specific_gene_expression_filename != "")
						{
							asGeneExpressionWriteTime = new FileInfo(case_.normal_allele_specific_gene_expression_filename).LastWriteTime;
							if (new FileInfo(case_.annotated_selected_variants_filename).LastWriteTime > asGeneExpressionWriteTime)
							{
								Console.WriteLine("AS gene expression normal file " + case_.normal_allele_specific_gene_expression_filename + " is older than its regional file " + case_.regional_expression_filename);
								allOK = false;
								continue;
							}
						}
					}
					else
					{
						if (case_.gene_expression_filename == "")
						{
							continue;
						}

						var geneExpressionWriteTime = new FileInfo(case_.gene_expression_filename).LastWriteTime;

						if (case_.regional_expression_filename == "")
						{
							Console.WriteLine("Gene expression file " + case_.gene_expression_filename + " exists, but the precursor regional expression file does not.");
							allOK = false;
							continue;
						}

						if (new FileInfo(case_.regional_expression_filename).LastWriteTime > geneExpressionWriteTime)
						{
							Console.WriteLine("Gene expression file " + case_.gene_expression_filename + " is older than its regional file " + case_.regional_expression_filename);
							allOK = false;
							continue;
						}
					}
				}
				return allOK;
			} // EvaluateDependencies

        } // ExpressionNearMutationsProcessingStage
#endif

        class AlleleSpecificExpressionNearMutationsProcessingStage : PerCaseProcessingStage
        {
            public AlleleSpecificExpressionNearMutationsProcessingStage() :
                base("Allele Specific Expression Near Mutations", "ExpressionNearMutations.exe", "-a", getCaseFile, getOneOffFile, getOutputFile)
            {
            }

            static GetCaseFile[] getCaseFile = {_ => _.maf_filename, _ => _.annotated_selected_variants_filename,
                       _ => _.tumor_copy_number_filename, _ => _.extracted_maf_lines_filename};

            static GetOneOffFile[] getOneOffFile = {stateOfTheWorld => stateOfTheWorld.configuration.finalResultsDirectory + ASETools.ASECorrectionFilename ,
                                    stateOfTheWorld => stateOfTheWorld.configuration.finalResultsDirectory + ASETools.PerGeneASEMapFilename};

            static GetCaseFile[] getOutputFile = {_ => _.tumor_allele_specific_gene_expression_filename,
                                                  _ => _.normal_rna_filename == "" ? _.tumor_allele_specific_gene_expression_filename : _.normal_allele_specific_gene_expression_filename};
        }// AlleleSpecificExpressionNearMutationsProcessingStage

        class ExpressionNearMutationsProcessingStage : PerCaseProcessingStage
        {
            public ExpressionNearMutationsProcessingStage() : base("Expression Near Mutations", "ExpressionNearMutations.exe", "", getCaseFile, null, getOutputFile)
            { }

            static GetCaseFile[] getCaseFile = { _ => _.maf_filename, _ => _.regional_expression_filename };
            static GetCaseFile[] getOutputFile = { _ => _.gene_expression_filename };
        }

#if true
        class ExtractReadsProcessingStage : PerCaseProcessingStage
        {
            public ExtractReadsProcessingStage(StateOfTheWorld stateOfTheWorld, bool dna_, bool tumor_) : 
                base("Extract " + (tumor_ ? "tumor" : "normal") + (dna_ ? " DNA" : " RNA") + " Reads ", "GenerateReadExtractionScript", 
                     (dna_ ? "-d" : "-r") + " " + (tumor_ ? "-t" : "-n") + " " + stateOfTheWorld.configuration.binariesDirectory + "GenerateConsolodatedExtractedReads.exe " + stateOfTheWorld.configuration.binariesDirectory + "samtools.exe ",
                     getCaseFile, null, null)
            {
                dna = dna_;
                tumor = tumor_;

                addDownloadableInputFile((case_, sotw) => GetDownloadableFile(tumor_, dna_, case_, sotw));
                addOutputFileGetter(_ => (!tumor && !dna && _.normal_rna_file_id == "") ? null : _.getReadsAtTentativeSelectedVariantsFilename(tumor, dna));
                addOutputFileGetter(_ => (!tumor && !dna && _.normal_rna_file_id == "") ? null : _.getReadsAtTentativeSelectedVariantsIndexFilename(tumor, dna));
                replaceGenerateCaseIdOutput((caseId, sotw) => makeCaseIdOutput(tumor, dna, sotw, caseId));
            } // ctor

            static FileIdAndExpectedMD5 GetDownloadableFile(bool tumor, bool dna, ASETools.Case case_, StateOfTheWorld stateOfTheWorld)
            {
                if (!tumor && !dna && case_.normal_rna_file_id == "") return null;

                return new FileIdAndExpectedMD5(case_.getDownloadedReadsFileId(tumor, dna), case_.getDownloadedReadsBamMD5(tumor, dna));
            } // GetDownloadableFile

            //                string outputFilename = ASETools.GoUpFilesystemLevels(ASETools.GetDirectoryFromPathname(inputFilename), 2) + stateOfTheWorld.configuration.derivedFilesDirectory + @"\" + case_.case_id + @"\" + fileId + outputExtension + " ";

            //outputSoFar += case_.case_id + " " + outputFilename;

            static string makeCaseIdOutput(bool tumor, bool dna, StateOfTheWorld stateOfTheWorld, string caseId)
            {
                var case_ = stateOfTheWorld.cases[caseId];
                if (!tumor && !dna && case_.normal_rna_file_id == "") return null;


                string inputFilename = case_.getDownloadedReadsFilename(tumor, dna);
                if (inputFilename == "")
                {
                    return "";
                }

                string outputFilename = ASETools.GoUpFilesystemLevels(ASETools.GetDirectoryFromPathname(inputFilename), 2) + stateOfTheWorld.configuration.derivedFilesDirectory + @"\" + case_.case_id + @"\" + case_.getDownloadedReadsFileId(tumor, dna) + 
                    ASETools.getReadsAtTentativeSelectedVariantsExtension(tumor, dna);
                return caseId + " " + outputFilename;
            }

            static GetCaseFile[] getCaseFile = { _ => _.tentative_selected_variants_filename, _ => _.extracted_maf_lines_filename };

            bool dna;
            bool tumor;
        } // ExtractReadsProcessingStage

#else //false

        class ExtractReadsProcessingStage : ProcessingStage
        {
            public ExtractReadsProcessingStage() { }

            public string GetStageName() { return "Extract Reads"; }

            public bool NeedsCases() { return true; }

            void HandleFileAndType(StateOfTheWorld stateOfTheWorld, ASETools.Case case_, string flagsString, string readsAtTentativeSelectedVariantsFilename, string readsAtTentativeSelectedVariantsIndexFilename, 
                string fileId, string md5Checksum, string inputFilename, string outputExtension, ref string outputSoFar, ref string outputSoFarHpc, StreamWriter script, ASETools.RandomizingStreamWriter hpcScript, 
                ref int nDone, ref int nAddedToScript, ref int nWaitingForPrerequisites)
            {
                if (fileId == "")
                {
                    //
                    // Sometimes there is no normal RNA, which shows up as an empty fileID.
                    //
                    return;
                }

                if ((readsAtTentativeSelectedVariantsFilename == "") != (readsAtTentativeSelectedVariantsIndexFilename == ""))
                {
                    Console.WriteLine("Exactly one of reads at selected variants and reads at selected variants index files exists: " + readsAtTentativeSelectedVariantsFilename + " " + readsAtTentativeSelectedVariantsIndexFilename);
                    return;
                }

                if (readsAtTentativeSelectedVariantsFilename != "")
                {
#if false// This is off because it's slow
                    var writeTime1 = new FileInfo(readsAtTentativeSelectedVariantsFilename).LastWriteTime;
                    var writeTime2 = new FileInfo(readsAtTentativeSelectedVariantsIndexFilename).LastWriteTime;

                    if (writeTime1 > writeTime2.AddDays(1) || writeTime2 > writeTime1.AddDays(1))
                    {
                        Console.WriteLine("Extracted reads and index files differ by more than one day: " + readsAtTentativeSelectedVariantsFilename + " " + readsAtTentativeSelectedVariantsIndexFilename);
                    }
#endif // false
                    nDone++;
                    return;
                }

                if (!stateOfTheWorld.fileDownloadedAndVerified(fileId, md5Checksum) || case_.tentative_selected_variants_filename == "" || case_.extracted_maf_lines_filename == "")
                {
                    nWaitingForPrerequisites++;
                    return;
                }

                nAddedToScript++;

                if (outputSoFar == "")
                {
                    outputSoFar = stateOfTheWorld.configuration.binariesDirectory + "GenerateReadExtractionScript " + stateOfTheWorld.configurationString + flagsString + " " + stateOfTheWorld.configuration.binariesDirectory + "GenerateConsolodatedExtractedReads.exe " +
                                stateOfTheWorld.configuration.binariesDirectory + "samtools.exe ";

                    outputSoFarHpc = jobAddString + stateOfTheWorld.configuration.hpcBinariesDirectory + "GenerateReadExtractionScript " + stateOfTheWorld.configurationString + flagsString + " " + stateOfTheWorld.configuration.hpcBinariesDirectory + "GenerateConsolodatedExtractedReads.exe " +
                                stateOfTheWorld.configuration.hpcBinariesDirectory + "samtools.exe ";
                }

                string outputFilename = ASETools.GoUpFilesystemLevels(ASETools.GetDirectoryFromPathname(inputFilename), 2) + stateOfTheWorld.configuration.derivedFilesDirectory + @"\" + case_.case_id + @"\" + fileId + outputExtension + " ";

                outputSoFar += case_.case_id + " " + outputFilename;
                outputSoFarHpc += case_.case_id + " " + outputFilename;

                if (Math.Max(outputSoFar.Count(), outputSoFarHpc.Count()) > 2000)
                {
                    script.WriteLine(outputSoFar);
                    hpcScript.WriteLine(outputSoFarHpc);

                    outputSoFar = "";
                    outputSoFarHpc = "";
                }
            }

            public void EvaluateStage(StateOfTheWorld stateOfTheWorld, StreamWriter script, ASETools.RandomizingStreamWriter hpcScript, StreamWriter linuxScript, StreamWriter azureScript, out List<string> filesToDownload, out int nDone, out int nAddedToScript, out int nWaitingForPrerequisites)
            {
                filesToDownload = null;
                nDone = 0;
                nAddedToScript = 0;
                nWaitingForPrerequisites = 0;

                string tumorDNAOutput = "";
                string tumorDNAHpcOutput = "";
                string normalDNAOutput = "";
                string normalDNAHpcOutput = "";
                string tumorRNAOutput = "";
                string tumorRNAHpcOutput = "";
                string normalRNAOutput = "";
                string normalRNAHpcOutput = "";

                foreach (var caseEntry in stateOfTheWorld.cases)
                {
                    var case_ = caseEntry.Value;

                    HandleFileAndType(stateOfTheWorld, case_, "-d -t", case_.tumor_dna_reads_at_tentative_selected_variants_filename, case_.tumor_dna_reads_at_tentative_selected_variants_index_filename, case_.tumor_dna_file_id,  case_.tumor_dna_file_bam_md5,  case_.tumor_dna_filename,  ASETools.tumorDNAReadsAtTentativeSelectedVariantsExtension,  ref tumorDNAOutput,  ref tumorDNAHpcOutput,  script, hpcScript, ref nDone, ref nAddedToScript, ref nWaitingForPrerequisites);
                    HandleFileAndType(stateOfTheWorld, case_, "-d -n", case_.normal_dna_reads_at_tentative_selected_variants_filename, case_.normal_dna_reads_at_tentative_selected_variants_index_filename, case_.normal_dna_file_id, case_.normal_dna_file_bam_md5, case_.normal_dna_filename, ASETools.normalDNAReadsAtTentativeSelectedVariantsExtension, ref normalDNAOutput, ref normalDNAHpcOutput, script, hpcScript, ref nDone, ref nAddedToScript, ref nWaitingForPrerequisites);
                    HandleFileAndType(stateOfTheWorld, case_, "-r -t", case_.tumor_rna_reads_at_tentative_selected_variants_filename, case_.tumor_rna_reads_at_tentative_selected_variants_index_filename, case_.tumor_rna_file_id,  case_.tumor_rna_file_bam_md5,  case_.tumor_rna_filename,  ASETools.tumorRNAReadsAtTentativeSelectedVariantsExtension,  ref tumorRNAOutput,  ref tumorRNAHpcOutput,  script, hpcScript, ref nDone, ref nAddedToScript, ref nWaitingForPrerequisites);
                    HandleFileAndType(stateOfTheWorld, case_, "-r -n", case_.normal_rna_reads_at_tentative_selected_variants_filename, case_.normal_rna_reads_at_tentative_selected_variants_index_filename, case_.normal_rna_file_id, case_.normal_rna_file_bam_md5, case_.normal_rna_filename, ASETools.normalRNAReadsAtTentativeSelectedVariantsExtension, ref normalRNAOutput, ref normalRNAHpcOutput, script, hpcScript, ref nDone, ref nAddedToScript, ref nWaitingForPrerequisites);
                }

                string[] outputs = { tumorDNAOutput, normalDNAOutput, tumorRNAOutput, normalRNAOutput };
                foreach (var output in outputs)
                {
                    if (output != "")
                    {
                        script.WriteLine(output);
                    }
                }

                string[] hpcOutputs = { tumorDNAHpcOutput, normalDNAHpcOutput, tumorRNAHpcOutput, normalRNAHpcOutput };
                foreach (var hpcOutput in hpcOutputs)
                {
                    if (hpcOutput != "")
                    {
                        hpcScript.WriteLine(hpcOutput);
                    }
                }
            } // EvaluateStage


            public bool EvaluateDependencies(StateOfTheWorld stateOfTheWorld)
            {
                // Fill this in later
                return true;
            } // EvaluateDependencies
        } // ExtractReadsProcessingStage
        
#endif // false

        class SelectGenesProcessingStage : ProcessingStage
        {
            public SelectGenesProcessingStage() { }

            public string GetStageName()
            {
                return "Select Genes";
            }

            public bool NeedsCases() { return true; }

            public void EvaluateStage(StateOfTheWorld stateOfTheWorld, StreamWriter script, ASETools.RandomizingStreamWriter hpcScript, StreamWriter linuxScript, StreamWriter azureScript, out List<string> filesToDownload, out int nDone, out int nAddedToScript, out int nWaitingForPrerequisites)
            {
                nDone = 0;
                nAddedToScript = 0;
                nWaitingForPrerequisites = 0;
                filesToDownload = new List<string>();

                if (stateOfTheWorld.selectedGenes != null)
                {
                    nDone++;
                    return;
                }

                foreach (var caseEntry in stateOfTheWorld.cases)
                {
                    if (caseEntry.Value.extracted_maf_lines_filename == "")
                    {
                        nWaitingForPrerequisites++;
                        return;
                    }
                }

                nAddedToScript++;
                script.WriteLine(stateOfTheWorld.configuration.binariesDirectory + "SelectGenes.exe" + stateOfTheWorld.configurationString);
                hpcScript.WriteLine(jobAddString + stateOfTheWorld.configuration.hpcBinariesDirectory + "SelectGenes.exe" + stateOfTheWorld.configurationString);
            }

            public bool EvaluateDependencies(StateOfTheWorld stateOfTheWorld)
            {
                if (stateOfTheWorld.selectedGenes == null)
                {
                    return true;    // No output means no violated dependencies
                }

                var selectedGenesWriteTime = new FileInfo(stateOfTheWorld.configuration.selectedGenesFilename).LastWriteTime;

                foreach (var caseEntry in stateOfTheWorld.cases)
                {
                    var case_ = caseEntry.Value;

                    if (case_.extracted_maf_lines_filename == "" )
                    {
                        Console.WriteLine("Dependency violation: case " + case_.case_id + " does not have extracted MAF lines, but we have selected genes.");
                        return false;   // Don't worry about the others, we need to regenerate the selected genes file regardless.
                    }

                    if (new FileInfo (case_.extracted_maf_lines_filename).LastWriteTime > selectedGenesWriteTime)
                    {
                        Console.WriteLine("Dependency violation: the selected genes file is older than an extracted MAF lines file " + case_.extracted_maf_lines_filename);
                        return false;
                    }
                }

                return true;
            } // EvaluateDependencies
        } // SelectGenesProcessingStage

        class CountMappedBasesProcessingStage : ProcessingStage
        {
            public CountMappedBasesProcessingStage() { }

            public string GetStageName() { return "Count Mapped Bases"; }

            public bool NeedsCases() { return true; }

            public void EvaluateStage(StateOfTheWorld stateOfTheWorld, StreamWriter script, ASETools.RandomizingStreamWriter hpcScript, StreamWriter linuxScript, StreamWriter azureScript, out List<string> filesToDownload, out int nDone, out int nAddedToScript, out int nWaitingForPrerequisites)
            {
                nDone = 0;
                nAddedToScript = 0;
                nWaitingForPrerequisites = 0;
                filesToDownload = null;

                int nOnCurrentCommandLine = 0;

                foreach (var caseEntry in stateOfTheWorld.cases)
                {
                    var case_ = caseEntry.Value;
                    HandleFile(stateOfTheWorld, script, hpcScript, case_.normal_dna_allcount_filename, case_.normal_dna_mapped_base_count_filename, ASETools.normalDNAMappedBaseCountExtension, ref nDone, ref nAddedToScript, ref nWaitingForPrerequisites, ref nOnCurrentCommandLine);
                    HandleFile(stateOfTheWorld, script, hpcScript, case_.tumor_dna_allcount_filename, case_.tumor_dna_mapped_base_count_filename, ASETools.tumorDNAMappedBaseCountExtension, ref nDone, ref nAddedToScript, ref nWaitingForPrerequisites, ref nOnCurrentCommandLine);
                    if (case_.normal_rna_file_id != "" && case_.normal_rna_file_id != null)
                    {
                        HandleFile(stateOfTheWorld, script, hpcScript, case_.normal_rna_allcount_filename, case_.normal_rna_mapped_base_count_filename, ASETools.normalRNAMappedBaseCountExtension, ref nDone, ref nAddedToScript, ref nWaitingForPrerequisites, ref nOnCurrentCommandLine);
                    }
                    HandleFile(stateOfTheWorld, script, hpcScript, case_.tumor_rna_allcount_filename, case_.tumor_rna_mapped_base_count_filename, ASETools.tumorRNAMappedBaseCountExtension, ref nDone, ref nAddedToScript, ref nWaitingForPrerequisites, ref nOnCurrentCommandLine);
                } // foreach case

                if (nOnCurrentCommandLine != 0)
                {
                    script.WriteLine();
                    hpcScript.WriteLine();
                }
            } // EvaluateStage

            void HandleFile(StateOfTheWorld stateOfTheWorld, StreamWriter script, ASETools.RandomizingStreamWriter hpcScript, string inputFilename, string existingOutputFilename, string outputExtension, 
                ref int nDone, ref int nAddedToScript, ref int nWaitingForPrerequisites, ref int nOnCurrentCommandLine)
            {
                if (existingOutputFilename != "")
                {
                    nDone++;
                    return;
                }

                if (inputFilename == "")
                {
                    nWaitingForPrerequisites++;
                    return;
                }

                if (nOnCurrentCommandLine == 0)
                {
                    script.Write(stateOfTheWorld.configuration.binariesDirectory + "CountMappedBases.exe" + stateOfTheWorld.configurationString);
                    hpcScript.Write(jobAddString + stateOfTheWorld.configuration.hpcBinariesDirectory + "CountMappedBases.exe" + stateOfTheWorld.configurationString);
                }

                script.Write(" " + inputFilename + " " + ASETools.GetDirectoryFromPathname(inputFilename) + @"\" + ASETools.GetFileIdFromPathname(inputFilename) + outputExtension);
                hpcScript.Write(" " + inputFilename + " " + ASETools.GetDirectoryFromPathname(inputFilename) + @"\" + ASETools.GetFileIdFromPathname(inputFilename) + outputExtension);

                nOnCurrentCommandLine++;
                if (nOnCurrentCommandLine >= 30)    // Much more than this generates too long command lines, with resulting unhappiness
                {
                    script.WriteLine();
                    hpcScript.WriteLine();
                    nOnCurrentCommandLine = 0;
                }

                nAddedToScript++;
            } // HandleFile

            public bool EvaluateDependencies(StateOfTheWorld stateOfTheWorld)
            {
                bool worked = true;
                foreach (var caseEntry in stateOfTheWorld.cases)
                {
                    var case_ = caseEntry.Value;

                    worked &= HandleDependency(case_.normal_dna_mapped_base_count_filename, case_.normal_dna_allcount_filename);
                    worked &= HandleDependency(case_.tumor_dna_mapped_base_count_filename, case_.tumor_dna_allcount_filename);
                    worked &= HandleDependency(case_.normal_rna_mapped_base_count_filename, case_.normal_rna_allcount_filename);
                    worked &= HandleDependency(case_.tumor_rna_mapped_base_count_filename, case_.tumor_rna_allcount_filename);
                }

                return worked;
            } // EvaluateDependencies

            bool HandleDependency(string baseCountFilename, string allcountFilename)
            {
                if (baseCountFilename == "")
                {
                    return true;
                }

                if (allcountFilename == "")
                {
                    Console.WriteLine("Base count file " + baseCountFilename + " exists, but its prerequisite allcount file does not.");
                    return false;
                }

                if (new FileInfo(baseCountFilename).LastWriteTime < new FileInfo(allcountFilename).LastWriteTime)
                {
                    Console.WriteLine("Base count file " + baseCountFilename + " is older than its predecessor " + allcountFilename);
                    return false;
                }

                return true;
            } // HandleDependency

        } // CountMappedBasesProcessingStage

        class GenerateScatterGraphsProcessingStage : ProcessingStage
        {
            public GenerateScatterGraphsProcessingStage() { }

            public string GetStageName() { return "Generate Scatter Graphs"; }

            public bool NeedsCases() { return true; }

            public void EvaluateStage(StateOfTheWorld stateOfTheWorld, StreamWriter script, ASETools.RandomizingStreamWriter hpcScript, StreamWriter linuxScript, StreamWriter azureScript, out List<string> filesToDownload, out int nDone, out int nAddedToScript, out int nWaitingForPrerequisites)
            {
                filesToDownload = null;
                nDone = 0;
                nAddedToScript = 0;
                nWaitingForPrerequisites = 0;

                bool missingAnything = false;

                if (stateOfTheWorld.scatterGraphsSummaryFile == "")
                {
                    missingAnything = true;
                } 
                //
                // Don't check that there are files for the selected genes, since they might not be there due to too small n.
                //


                if (!missingAnything)
                {
                    nDone = 1;
                    return;
                }

                foreach (var caseEntry in stateOfTheWorld.cases)
                {
                    var case_ = caseEntry.Value;

                    if (case_.annotated_selected_variants_filename == "" || case_.tumor_dna_mapped_base_count_filename == "" || case_.tumor_rna_mapped_base_count_filename == "")
                    {
                        nWaitingForPrerequisites = 1;
                        return;
                    }
                }

                nAddedToScript = 1;

                script.WriteLine(stateOfTheWorld.configuration.binariesDirectory + "GenerateScatterGraphs.exe" + stateOfTheWorld.configurationString);
                hpcScript.WriteLine(jobAddString + stateOfTheWorld.configuration.hpcBinariesDirectory + "GenerateScatterGraphs.exe" + stateOfTheWorld.configurationString);
            } // EvaluateStage

            public bool EvaluateDependencies(StateOfTheWorld stateOfTheWorld)
            {
                DateTime oldestFile;
                if (stateOfTheWorld.scatterGraphsSummaryFile == "")
                {
                    return true;
                }
 
                oldestFile = new FileInfo(stateOfTheWorld.scatterGraphsSummaryFile).LastWriteTime;
                foreach (var selectedGene in stateOfTheWorld.selectedGenes)
                {
                    if (!stateOfTheWorld.scatterGraphsByHugoSymbol.ContainsKey(selectedGene.Hugo_Symbol))
                    {
                        return true;
                    }

                    var date = new FileInfo(stateOfTheWorld.scatterGraphsByHugoSymbol[selectedGene.Hugo_Symbol]).LastWriteTime;
                    if (date < oldestFile) {
                        oldestFile = date;
                    }
                }

                //
                // Now look at the dependencies.
                //
                foreach (var caseEntry in stateOfTheWorld.cases)
                {
                    var case_ = caseEntry.Value;

                    if (case_.annotated_selected_variants_filename == "" || new FileInfo(case_.annotated_selected_variants_filename).LastWriteTime > oldestFile)
                    {
                        Console.WriteLine("Case " + case_.case_id + " either doesn't have an annotated selected variants file, or it is newer than a gene scatter graph file that depends on it.");
                        return false;
                    }

                    if (case_.tumor_dna_mapped_base_count_filename == "" || new FileInfo(case_.tumor_dna_mapped_base_count_filename).LastWriteTime > oldestFile ||
                        case_.tumor_rna_mapped_base_count_filename == "" || new FileInfo(case_.tumor_rna_mapped_base_count_filename).LastWriteTime > oldestFile)
                    {
                        Console.WriteLine("Case " + case_.case_id + " either doesn't have a tumor mapped base count file, or it is newer than a gene scatter graph file that depends on it.");
                        return false;
                    }
                }

                return true;
            } // EvaluateDependencies

        } // GenerateScatterGraphsProcessingStage

        class GenerateAllLociProcessingStage : ProcessingStage
        {
            public GenerateAllLociProcessingStage() { }

            public string GetStageName() { return "Generate All Loci Files"; }

            public bool NeedsCases() { return false; }

            public void EvaluateStage(StateOfTheWorld stateOfTheWorld, StreamWriter script, ASETools.RandomizingStreamWriter hpcScript, StreamWriter linuxScript, StreamWriter azureScript, out List<string> filesToDownload, out int nDone, out int nAddedToScript, out int nWaitingForPrerequisites)
            {
                nDone = 0;
                nAddedToScript = 0;
                nWaitingForPrerequisites = 0;   // There are no prerequesites for this, so it will always stay zero
                filesToDownload = null;

                for (int chromosomeNumber = 1; chromosomeNumber <= ASETools.nHumanAutosomes; chromosomeNumber++)
                {
                    if (File.Exists(stateOfTheWorld.configuration.chromosomeMapsDirectory + "chr" + chromosomeNumber + ASETools.allLociExtension))
                    {
                        nDone++;
                    } else
                    {
                        script.WriteLine(stateOfTheWorld.configuration.binariesDirectory + "GenerateReadsForRepetitiveRegionDetection.exe " + stateOfTheWorld.configuration.indexDirectory + " " + chromosomeNumber + " " +
                            stateOfTheWorld.configuration.chromosomeMapsDirectory +"chr" + chromosomeNumber +
                            ASETools.allLociExtension + " " + stateOfTheWorld.configuration.readLengthForRepetitiveRegionDetection);
                        nAddedToScript++;
                    }
                }
            }

            public bool EvaluateDependencies(StateOfTheWorld stateOfTheWorld) { return true; }   // Really no dependencies for this unless the reference genome changes
        } // GenerateAllLociProcessingStage

        class AlignAllLociProcessingStage : ProcessingStage
        {
            public AlignAllLociProcessingStage() { }

            public string GetStageName() { return "Align all loci"; }

            public bool NeedsCases() { return false; }

            public void EvaluateStage(StateOfTheWorld stateOfTheWorld, StreamWriter script, ASETools.RandomizingStreamWriter hpcScript, StreamWriter linuxScript, StreamWriter azureScript, out List<string> filesToDownload, out int nDone, out int nAddedToScript, out int nWaitingForPrerequisites)
            {
                nDone = 0;
                nAddedToScript = 0;
                nWaitingForPrerequisites = 0;
                filesToDownload = null;

                for (int chromosomeNumber = 1; chromosomeNumber <= ASETools.nHumanAutosomes; chromosomeNumber++)
                {
                    if (File.Exists(stateOfTheWorld.configuration.chromosomeMapsDirectory + "chr" + chromosomeNumber + ASETools.allLociAlignedExtension))
                    {
                        nDone++;
                    }
                    else if (!File.Exists(stateOfTheWorld.configuration.chromosomeMapsDirectory + "chr" + chromosomeNumber + ASETools.allLociExtension))
                    {
                        nWaitingForPrerequisites++;
                    }
                    else
                    {
                        script.WriteLine(stateOfTheWorld.configuration.binariesDirectory + "snap.exe single " + stateOfTheWorld.configuration.localIndexDirectory + " " + stateOfTheWorld.configuration.chromosomeMapsDirectory + "chr" + chromosomeNumber +
                            ASETools.allLociExtension + " -o " + stateOfTheWorld.configuration.chromosomeMapsDirectory +"chr" + chromosomeNumber + ASETools.allLociAlignedExtension + " -om 3 -omax 1 -x -D 3 -map -mrl 48 -=");
                        nAddedToScript++;
                    }

                    if (File.Exists(stateOfTheWorld.configuration.chromosomeMapsDirectory + "transcriptome_" + chromosomeNumber + ASETools.allLociAlignedExtension))
                    {
                        nDone++;
                    } else if (!File.Exists(stateOfTheWorld.configuration.chromosomeMapsDirectory + "transcriptome_" + chromosomeNumber + ASETools.allLociExtension))
                    {
                        nWaitingForPrerequisites++;
                    } else
                    {
                        script.WriteLine(stateOfTheWorld.configuration.binariesDirectory + "snap.exe single " + stateOfTheWorld.configuration.chromosomeMapsDirectory + ASETools.generatedTranscriptomeIndexName + " " +
                                         stateOfTheWorld.configuration.chromosomeMapsDirectory + "transcriptome_" + chromosomeNumber + ASETools.allLociExtension +
                                         " -o " + stateOfTheWorld.configuration.chromosomeMapsDirectory + "transcriptome_" + chromosomeNumber + ASETools.allLociAlignedExtension + " -om 3 -x -D 3 -map -mrl 48 -="); // omit -omax for transcriptome, because some mappings might be to different contigs that then themselves map back to the same genome location
                        nAddedToScript++;
                    }
                } // for each chromosome
            }

            public bool EvaluateDependencies(StateOfTheWorld stateOfTheWorld) { return true; }   // Really no dependencies for this unless the reference genome changes, since the input files are effectively constants

        } // AlignAllLociProcessingStage

        class RepetitveRegionMapProcessingStage : ProcessingStage
        {
            public RepetitveRegionMapProcessingStage() { }

            public string GetStageName() { return "Make Repetitive Region Map"; }
            public bool NeedsCases() { return false; }
            public void EvaluateStage(StateOfTheWorld stateOfTheWorld, StreamWriter script, ASETools.RandomizingStreamWriter hpcScript, StreamWriter linuxScript, StreamWriter azureScript, out List<string> filesToDownload, out int nDone, out int nAddedToScript, out int nWaitingForPrerequisites)
            {
                nDone = 0;
                nAddedToScript = 0;
                nWaitingForPrerequisites = 0;
                filesToDownload = null;
                
                if (File.Exists(stateOfTheWorld.configuration.redundantChromosomeRegionFilename))
                {
                    nDone++;
                    return;
                }

                for (int chromosomeNumber = 1; chromosomeNumber <= ASETools.nHumanAutosomes; chromosomeNumber++)
                {
                    if (!File.Exists(stateOfTheWorld.configuration.chromosomeMapsDirectory + "chr" + chromosomeNumber + ASETools.allLociAlignedExtension))
                    {
                        nWaitingForPrerequisites++;
                        return;
                    }

                    if (!File.Exists(stateOfTheWorld.configuration.chromosomeMapsDirectory + "transcriptome_" + chromosomeNumber + ASETools.allLociAlignedExtension))
                    {
                        nWaitingForPrerequisites++;
                        return;
                    }
                }

                script.WriteLine(stateOfTheWorld.configuration.binariesDirectory + "MakeRepetitiveRegionMap.exe" + stateOfTheWorld.configurationString);
                nAddedToScript++;
            }

            public bool EvaluateDependencies(StateOfTheWorld stateOfTheWorld) { return true; }   // Really no dependencies for this unless the reference genome changes, since the input files are effectively constants
        } // RepetitveRegionMapProcessingStage

        class OverallDistributionProcessingStage : ProcessingStage
        {
            public OverallDistributionProcessingStage() { }

            public string GetStageName() { return "Overall Distribution"; }

            public bool NeedsCases() { return true; }

            public void EvaluateStage(StateOfTheWorld stateOfTheWorld, StreamWriter script, ASETools.RandomizingStreamWriter hpcScript, StreamWriter linuxScript, StreamWriter azureScript, out List<string> filesToDownload, out int nDone, out int nAddedToScript, out int nWaitingForPrerequisites)
            {
                nDone = 0;
                nAddedToScript = 0;
                nWaitingForPrerequisites = 0;
                filesToDownload = null;

                //
                // There are two versions: one with and one without correction.  This is because the uncorrected version is used to generate the correction, which is in turn used to generate the
                // corrected version.
                //

                if (File.Exists(stateOfTheWorld.configuration.finalResultsDirectory + ASETools.UncorrectedOverallASEFilename))
                {
                    nDone++;
                } else if (!stateOfTheWorld.cases.Select(x => x.Value).All(x => x.annotated_selected_variants_filename != "" && (x.tumor_copy_number_filename != "" || stateOfTheWorld.configuration.isBeatAML) && (x.normal_copy_number_filename != "" || x.normal_copy_number_file_id == ""))
                           || !File.Exists(stateOfTheWorld.configuration.finalResultsDirectory + ASETools.PerGeneASEMapFilename)
                           || !File.Exists(stateOfTheWorld.configuration.redundantChromosomeRegionFilename))
                {
                    nWaitingForPrerequisites++;
                } else
                {
                    script.WriteLine(stateOfTheWorld.configuration.binariesDirectory + "OverallDistribution.exe" + stateOfTheWorld.configurationString);
                    nAddedToScript++;
                }

                if (File.Exists(stateOfTheWorld.configuration.finalResultsDirectory + ASETools.CorrectedOverallASEFilename))
                {
                    nDone++;
                }
                else if (!stateOfTheWorld.cases.Select(x => x.Value).All(x => x.annotated_selected_variants_filename != "" && x.tumor_copy_number_filename != "" && (x.normal_copy_number_filename != "" || x.normal_copy_number_file_id == "")) ||
                    !File.Exists(stateOfTheWorld.configuration.finalResultsDirectory + ASETools.UncorrectedOverallASEFilename)
                    || !File.Exists(stateOfTheWorld.configuration.finalResultsDirectory + ASETools.ASECorrectionFilename)
                    || !File.Exists(stateOfTheWorld.configuration.finalResultsDirectory + ASETools.PerGeneASEMapFilename))
                { 
                    nWaitingForPrerequisites++;
                }
                else
                {
                    script.WriteLine(stateOfTheWorld.configuration.binariesDirectory + "OverallDistribution.exe" + stateOfTheWorld.configurationString + " -c");
                    nAddedToScript++;
                }
            } // EvaluateStage

            public bool EvaluateDependencies(StateOfTheWorld stateOfTheWorld) { return true; }

        } // OverallDistributionProcessingStage

        class CorrectionProcessingStage: SingleOutputProcessingStage
        {
            public CorrectionProcessingStage() : base("ASE Correction", true, "GenerateASECorrection.exe", "", null, getOneOffInputs, getOutput) { }

            static GetOneOffFile[] getOneOffInputs = { _ => _.configuration.finalResultsDirectory + ASETools.UncorrectedOverallASEFilename, _ => _.configuration.finalResultsDirectory + ASETools.TumorRNAReadDepthDistributionFilename };
            static GetOneOffFile[] getOutput = { _ => _.configuration.finalResultsDirectory + ASETools.ASECorrectionFilename };
        } // CorrectionProcessingStage

#if false
        class CorrectionProcessingStage : ProcessingStage
        {
            public CorrectionProcessingStage() { }

            public string GetStageName() { return "ASE Correction"; }

            public bool NeedsCases() { return true; }

            public void EvaluateStage(StateOfTheWorld stateOfTheWorld, StreamWriter script, ASETools.RandomizingStreamWriter hpcScript, StreamWriter linuxScript, StreamWriter azureScript, out List<string> filesToDownload, out int nDone, out int nAddedToScript, out int nWaitingForPrerequisites)
            {
                nDone = 0;
                nAddedToScript = 0;
                nWaitingForPrerequisites = 0;
                filesToDownload = null;

                if (File.Exists(stateOfTheWorld.configuration.finalResultsDirectory + ASETools.ASECorrectionFilename))
                {
                    nDone++;
                    return;
                }

                if (!File.Exists(stateOfTheWorld.configuration.finalResultsDirectory + ASETools.UncorrectedOverallASEFilename) || !File.Exists(stateOfTheWorld.configuration.finalResultsDirectory + ASETools.TumorRNAReadDepthDistributionFilename))
                {
                    nWaitingForPrerequisites++;
                    return;
                }

                script.WriteLine(stateOfTheWorld.configuration.binariesDirectory + "GenerateASECorrection.exe" + stateOfTheWorld.configurationString);
                nAddedToScript++;
            }

            public bool EvaluateDependencies(StateOfTheWorld stateOfTheWorld) { return true; }

        } // CorrectionProcessingStage
#endif // false

        class AllSitesReadDepthProcessingStage : ProcessingStage
        {
            public AllSitesReadDepthProcessingStage() { }

            public string GetStageName() { return "All Sites Read Depth"; }

            public bool NeedsCases() { return true; }

            public void EvaluateStage(StateOfTheWorld stateOfTheWorld, StreamWriter script, ASETools.RandomizingStreamWriter hpcScript, StreamWriter linuxScript, StreamWriter azureScript, out List<string> filesToDownload, out int nDone, out int nAddedToScript, out int nWaitingForPrerequisites)
            {
                filesToDownload = null;
                nDone = 0;
                nAddedToScript = 0;
                nWaitingForPrerequisites = 0;

                if (File.Exists(stateOfTheWorld.configuration.finalResultsDirectory + ASETools.AllSitesReadDepthFilename))
                {
                    nDone = 1;
                    return;
                }

                if (stateOfTheWorld.cases.Select(x => x.Value).Any(x => x.tumor_dna_allcount_filename == "" || x.normal_dna_allcount_filename == "" || x.tumor_rna_allcount_filename == "" || x.normal_rna_allcount_filename == "" && x.normal_rna_file_id != ""))
                {
                    nWaitingForPrerequisites = 1;
                    return;
                }

                nAddedToScript = 1;
                script.WriteLine(stateOfTheWorld.configuration.binariesDirectory + "AllSitesReadDepthDistribution.exe" + stateOfTheWorld.configurationString);
            } // EvaluateStage

            public bool EvaluateDependencies(StateOfTheWorld stateOfTheWorld) { return true; }

        } // AllSitesReadDepthProcessingStage

        class ExpressionByMutationCountProcessingStage : ProcessingStage
        {
            public ExpressionByMutationCountProcessingStage() { }

            public string GetStageName() { return "Expression by Mutation Count"; }

            public bool NeedsCases() { return true; }

            public void EvaluateStage(StateOfTheWorld stateOfTheWorld, StreamWriter script, ASETools.RandomizingStreamWriter hpcScript, StreamWriter linuxScript, StreamWriter azureScript, out List<string> filesToDownload, out int nDone, out int nAddedToScript, out int nWaitingForPrerequisites)
            {
                nDone = 0;
                nAddedToScript = 0;
                nWaitingForPrerequisites = 0;
                filesToDownload = null;

                if (File.Exists(stateOfTheWorld.configuration.finalResultsDirectory + "AlleleSpecificExpressionDistributionByMutationCount.txt"))
                {
                    nDone = 1;
                    return;
                }

                if (stateOfTheWorld.cases.Select(x => x.Value).Any(x => x.tumor_allele_specific_gene_expression_filename == "") || stateOfTheWorld.scatterGraphsSummaryFile == "")
                {
                    nWaitingForPrerequisites = 1;
                    return;
                }

                script.WriteLine(stateOfTheWorld.configuration.binariesDirectory + "ExpressionByMutationCount.exe" + stateOfTheWorld.configurationString + " -a");
                nAddedToScript = 1;
            }

            public bool EvaluateDependencies(StateOfTheWorld stateOfTheWorld) { return true; }

        } // ExpressionByMutationCountProcessingStage

        class BonferroniProcessingStage : ProcessingStage
        {
            public BonferroniProcessingStage() { }

            public string GetStageName() { return "Apply Bonferroni Correction"; }

            public bool NeedsCases() { return true; }

            public void EvaluateStage(StateOfTheWorld stateOfTheWorld, StreamWriter script, ASETools.RandomizingStreamWriter hpcScript, StreamWriter linuxScript, StreamWriter azureScript, out List<string> filesToDownload, out int nDone, out int nAddedToScript, out int nWaitingForPrerequisites)

            {
                nDone = 0;
                nAddedToScript = 0;
                nWaitingForPrerequisites = 0;
                filesToDownload = null;

                if (File.Exists(stateOfTheWorld.configuration.finalResultsDirectory + "AlleleSpecificExpressionDistributionByMutationCount_bonferroni.txt"))
                {
                    nDone = 1;
                    return;
                }

                if (!File.Exists(stateOfTheWorld.configuration.finalResultsDirectory + "AlleleSpecificExpressionDistributionByMutationCount.txt"))
                {
                    nWaitingForPrerequisites = 1;
                    return;
                }

                script.WriteLine(stateOfTheWorld.configuration.binariesDirectory + "ApplyBonferroniCorrection.exe" + stateOfTheWorld.configurationString);
                nAddedToScript = 1;
            }

            public bool EvaluateDependencies(StateOfTheWorld stateOfTheWorld) { return true; }

        } // BonferroniProcessingStage

        class ASEConsistencyProcessingStage : ProcessingStage
        {
            public ASEConsistencyProcessingStage() { }
            public string GetStageName() { return "ASE Consistency"; }

            public bool NeedsCases() { return true; }

            public void EvaluateStage(StateOfTheWorld stateOfTheWorld, StreamWriter script, ASETools.RandomizingStreamWriter hpcScript, StreamWriter linuxScript, StreamWriter azureScript, out List<string> filesToDownload, out int nDone, out int nAddedToScript, out int nWaitingForPrerequisites)
            {
                nDone = 0;
                nAddedToScript = 0;
                nWaitingForPrerequisites = 0;
                filesToDownload = null;

                if (File.Exists(stateOfTheWorld.configuration.finalResultsDirectory + ASETools.ASEConsistencyFilename))
                {
                    nDone = 1;
                    return;
                }

                if (!File.Exists(stateOfTheWorld.configuration.finalResultsDirectory + ASETools.ASECorrectionFilename) || stateOfTheWorld.cases.Select(x => x.Value).Any(x => x.annotated_selected_variants_filename == "" || x.tumor_copy_number_filename == "") ||
                    !File.Exists(stateOfTheWorld.configuration.finalResultsDirectory + ASETools.PerGeneASEMapFilename))
                {
                    nWaitingForPrerequisites = 1;
                    return;
                }

                script.WriteLine(stateOfTheWorld.configuration.binariesDirectory + "ASEConsistency.exe" + stateOfTheWorld.configurationString);
                nAddedToScript = 1;
            }

            public bool EvaluateDependencies(StateOfTheWorld stateOfTheWorld) { return true; }

        } // ASEConsistencyProcessingStage

        class ASEMapProcessingStage : ProcessingStage
        {
            public ASEMapProcessingStage() { }
            public string GetStageName() { return "ASE Map"; }
            public bool NeedsCases() { return true; }
            public void EvaluateStage(StateOfTheWorld stateOfTheWorld, StreamWriter script, ASETools.RandomizingStreamWriter hpcScript, StreamWriter linuxScript, StreamWriter azureScript, out List<string> filesToDownload, out int nDone, out int nAddedToScript, out int nWaitingForPrerequisites)
            {
                nDone = 0;
                nAddedToScript = 0;
                nWaitingForPrerequisites = 0;
                filesToDownload = null;

                if (File.Exists(stateOfTheWorld.configuration.finalResultsDirectory + ASETools.PerGeneASEMapFilename) && 
                    File.Exists(stateOfTheWorld.configuration.finalResultsDirectory + ASETools.ASEMapFilename))
                {
                    nDone = 1;
                    return;
                }

                if (stateOfTheWorld.cases.Select(x => x.Value).
                    Any(c => c.annotated_selected_variants_filename == "" || c.tumor_rna_mapped_base_count_filename == ""
                    || c.normal_rna_file_id != "" && c.normal_rna_mapped_base_count_filename == ""))
                {
                    nWaitingForPrerequisites = 1;
                    return;
                }

                script.WriteLine(stateOfTheWorld.configuration.binariesDirectory + "ASEMap.exe" + stateOfTheWorld.configurationString);
                nAddedToScript = 1;
            } // EvaluateStage

            public bool EvaluateDependencies(StateOfTheWorld stateOfTheWorld) { return true; }

        } // ASEMapProcessingStage

        class ZeroOneTwoProcessingStage : ProcessingStage
        {
            public ZeroOneTwoProcessingStage() { }

            public string GetStageName() { return "Make 012 Graphs"; }

            public bool NeedsCases() { return true; }

            public void EvaluateStage(StateOfTheWorld stateOfTheWorld, StreamWriter script, ASETools.RandomizingStreamWriter hpcScript, StreamWriter linuxScript, StreamWriter azureScript, out List<string> filesToDownload, out int nDone, out int nAddedToScript, out int nWaitingForPrerequisites)
            {
                filesToDownload = new List<string>();
                nDone = 0;
                nAddedToScript = 0;
                nWaitingForPrerequisites = 0;

                if (File.Exists(stateOfTheWorld.configuration.zero_one_two_directory + "TP53-20.txt")) // NB: Assumes p53 in gene is significant
                {
                    nDone = 1;
                    return;
                }

                if (stateOfTheWorld.cases.Select(x => x.Value).Any(x => x.annotated_selected_variants_filename == "") ||
                    !File.Exists(stateOfTheWorld.configuration.finalResultsDirectory + "AlleleSpecificExpressionDistributionByMutationCount_bonferroni.txt"))
                {
                    nWaitingForPrerequisites = 1;
                    return;
                }

                nAddedToScript = 1;
                script.WriteLine(stateOfTheWorld.configuration.binariesDirectory + "MakeSignificant012Graphs.exe" + stateOfTheWorld.configurationString);

            } // EvaluateStage

            public bool EvaluateDependencies(StateOfTheWorld stateOfTheWorld) { return true; }


        } // ZeroOneTwoProcessingStage

        class MannWhitneyProcessingStage : ProcessingStage
        {
            public MannWhitneyProcessingStage() { }

            public string GetStageName() { return "Mann-Whitney"; }

            public bool NeedsCases() { return true; }

            public void EvaluateStage(StateOfTheWorld stateOfTheWorld, StreamWriter script, ASETools.RandomizingStreamWriter hpcScript, StreamWriter linuxScript, StreamWriter azureScript, out List<string> filesToDownload, out int nDone, out int nAddedToScript, out int nWaitingForPrerequisites)
            {
                filesToDownload = new List<string>();
                nDone = 0;
                nAddedToScript = 0;
                nWaitingForPrerequisites = 0;

                if (File.Exists(stateOfTheWorld.configuration.geneScatterGraphsDirectory + ASETools.mannWhitneyFilename))
                {
                    nDone = 1;
                    return;
                }

                if (stateOfTheWorld.scatterGraphsSummaryFile == "")
                {
                    nWaitingForPrerequisites = 1;
                    return;
                }

                script.WriteLine(stateOfTheWorld.configuration.binariesDirectory + "MannWhitney.exe" + stateOfTheWorld.configurationString);
                nAddedToScript = 1;
            }

            public bool EvaluateDependencies(StateOfTheWorld stateOfTheWorld) { return true; }
        } // MannWhitneyProcessingStage

        class PerCaseASEProcessingStage : ProcessingStage
        {
            public PerCaseASEProcessingStage() { }
            public string GetStageName() { return "Per-case ASE"; }

            public bool NeedsCases() { return true; }

            public void EvaluateStage(StateOfTheWorld stateOfTheWorld, StreamWriter script, ASETools.RandomizingStreamWriter hpcScript, StreamWriter linuxScript, StreamWriter azureScript, out List<string> filesToDownload, out int nDone, out int nAddedToScript, out int nWaitingForPrerequisites)
            {
                filesToDownload = new List<string>();
                nDone = 0;
                nAddedToScript = 0;
                nWaitingForPrerequisites = 0;

                if (File.Exists(stateOfTheWorld.configuration.finalResultsDirectory + ASETools.PerCaseASEFilename))
                {
                    nDone = 1;
                    return;
                }

                if (stateOfTheWorld.cases.Select(x => x.Value).Any(x => x.annotated_selected_variants_filename == "") || !File.Exists(stateOfTheWorld.configuration.finalResultsDirectory + ASETools.PerGeneASEMapFilename)
                    || !File.Exists(stateOfTheWorld.configuration.finalResultsDirectory + ASETools.ASECorrectionFilename))
                {
                    nWaitingForPrerequisites = 1;
                    return;
                }

                script.WriteLine(stateOfTheWorld.configuration.binariesDirectory + "ComputePerCaseASE.exe" + stateOfTheWorld.configurationString);
                nAddedToScript = 1;
            }

            public bool EvaluateDependencies(StateOfTheWorld stateOfTheWorld) { return true; }
        } // PerCaseASEProcessingStage

        class CategorizeTumorsProcessingStage : ProcessingStage
        {
            public CategorizeTumorsProcessingStage() { }
            public string GetStageName() { return "Categorize tumors"; }

            public bool NeedsCases() { return true; }

            public void EvaluateStage(StateOfTheWorld stateOfTheWorld, StreamWriter script, ASETools.RandomizingStreamWriter hpcScript, StreamWriter linuxScript, StreamWriter azureScript, out List<string> filesToDownload, out int nDone, out int nAddedToScript, out int nWaitingForPrerequisites)
            {
                filesToDownload = new List<string>();
                nDone = 0;
                nAddedToScript = 0;
                nWaitingForPrerequisites = 0;

                if (File.Exists(stateOfTheWorld.configuration.finalResultsDirectory + ASETools.Configuration.geneCategorizationFilename))
                {
                    nDone = 1;
                    return;
                }

                if (stateOfTheWorld.scatterGraphsSummaryFile == "")
                {
                    nWaitingForPrerequisites = 1;
                    return;
                }

                script.WriteLine(stateOfTheWorld.configuration.binariesDirectory + "CategorizeTumors.exe" + stateOfTheWorld.configurationString);
                nAddedToScript = 1;
            }

            public bool EvaluateDependencies(StateOfTheWorld stateOfTheWorld) { return true; }
        } // PerCaseASEProcessingStage

        class SelectRegulatoryMAFLinesProcessingStage : ProcessingStage
        {
            public SelectRegulatoryMAFLinesProcessingStage() { }
            public string GetStageName() { return "Select Regulatory MAF Lines"; }
            public bool NeedsCases() { return true; }
            public void EvaluateStage(StateOfTheWorld stateOfTheWorld, StreamWriter script, ASETools.RandomizingStreamWriter hpcScript, StreamWriter linuxScript, StreamWriter azureScript, out List<string> filesToDownload, out int nDone, out int nAddedToScript, out int nWaitingForPrerequisites)
            {
                filesToDownload = new List<string>();
                nDone = 0;
                nAddedToScript = 0;
                nWaitingForPrerequisites = 0;

                nDone = stateOfTheWorld.cases.Where(x => x.Value.selected_regulatory_maf_filename != "").Count();
                nWaitingForPrerequisites = stateOfTheWorld.cases.Where(x => x.Value.selected_regulatory_maf_filename == "" && x.Value.all_maf_lines_filename == "").Count();

                var toDo = stateOfTheWorld.cases.Select(x => x.Value).Where(x => x.selected_regulatory_maf_filename == "" && x.all_maf_lines_filename != "").ToList();
                nAddedToScript = toDo.Count();

                const int nPerLine = 100;
                for (int i = 0; i < nAddedToScript; i++)
                {
                    if (i % nPerLine == 0)
                    {
                        if (i != 0)
                        {
                            script.WriteLine();
                        }

                        script.Write(stateOfTheWorld.configuration.binariesDirectory + "SelectMutationsInReglatoryRegions.exe" + stateOfTheWorld.configurationString); // Yes, it's spelled wrong.  It's a giant pain to fix a typo in an app name, so I'm just leaving it.
                    }

                    script.Write(" " + toDo[i].case_id);
                }

                if (nAddedToScript != 0)
                {
                    script.WriteLine();
                }

            }
            public bool EvaluateDependencies(StateOfTheWorld stateOfTheWorld) { return true; }
        } // SelectRegulatoryMAFLinesProcessingStage

        class MappedBaseCountDistributionProcessingStage : ProcessingStage
        {
            public MappedBaseCountDistributionProcessingStage() { }
            public string GetStageName() { return "Mapped base count distruibution"; }
            public bool NeedsCases() { return true; }
            public void EvaluateStage(StateOfTheWorld stateOfTheWorld, StreamWriter script, ASETools.RandomizingStreamWriter hpcScript, StreamWriter linuxScript, StreamWriter azureScript, out List<string> filesToDownload, out int nDone, out int nAddedToScript, out int nWaitingForPrerequisites)
            {
                filesToDownload = new List<string>();
                nDone = 0;
                nAddedToScript = 0;
                nWaitingForPrerequisites = 0;

                if (File.Exists(stateOfTheWorld.configuration.finalResultsDirectory + ASETools.mappedBaseCountDistributionFilename))
                {
                    nDone = 1;
                    return;
                }

                if (stateOfTheWorld.cases.Select(x => x.Value).Any(x => x.tumor_dna_mapped_base_count_filename == "" || x.tumor_rna_mapped_base_count_filename == "" || x.normal_dna_mapped_base_count_filename == "" || x.normal_rna_file_id != "" && x.normal_rna_mapped_base_count_filename == ""))
                {
                    nWaitingForPrerequisites = 1;
                    return;
                }

                script.WriteLine(stateOfTheWorld.configuration.binariesDirectory + "MappedBaseCountDistribution.exe" + stateOfTheWorld.configurationString);
                nAddedToScript = 1;
            }

            public bool EvaluateDependencies(StateOfTheWorld stateOfTheWorld) { return true; }

        } // MappedBaseCountDistributionProcessingStage

#if false
        class AnnotateRegulatoryRegionsProcessingStage : ProcessingStage
        {
            public AnnotateRegulatoryRegionsProcessingStage() { }
            public string GetStageName() { return "Annotate Regulatory Regions"; }

            public bool NeedsCases() { return true; }
            public void EvaluateStage(StateOfTheWorld stateOfTheWorld, StreamWriter script, ASETools.RandomizingStreamWriter hpcScript, StreamWriter linuxScript, StreamWriter azureScript, out List<string> filesToDownload, out int nDone, out int nAddedToScript, out int nWaitingForPrerequisites)
            {
                filesToDownload = new List<string>();
                nDone = 0;
                nAddedToScript = 0;
                nWaitingForPrerequisites = 0;

                nDone = stateOfTheWorld.cases.Where(x => x.Value.annotated_regulatory_regions_filename != "" && x.Value.annotated_geneHancer_lines_filename != "").Count();
                if (stateOfTheWorld.configuration.encodeBEDFile == "" || stateOfTheWorld.configuration.geneHancerFilename == "")
                {
                    nWaitingForPrerequisites = 1;
                    return;
                }
                nWaitingForPrerequisites = stateOfTheWorld.cases.Select(x => x.Value).Where(x => x.annotated_regulatory_regions_filename == "" && (x.tumor_dna_allcount_filename == "" || x.selected_regulatory_maf_filename == "")).Count();

                int nOnCurrentLine = 0;
                foreach (var case_ in stateOfTheWorld.cases.Select(x => x.Value).Where(x => 
                (x.annotated_regulatory_regions_filename == ""  || x.annotated_geneHancer_lines_filename == "") && 
                x.tumor_dna_allcount_filename != "" && x.selected_regulatory_maf_filename != ""))
                {
                    if (nOnCurrentLine == 0)
                    {
                        script.Write(stateOfTheWorld.configuration.binariesDirectory + "AnnotateRegulatoryRegions.exe" + stateOfTheWorld.configurationString);
                    }

                    script.Write(" " + case_.case_id);
                    nOnCurrentLine++;

                    if (nOnCurrentLine >= 60)
                    {
                        script.WriteLine();
                        nOnCurrentLine = 0;
                    }

                    nAddedToScript++;
                }

                if (nOnCurrentLine != 0)
                {
                    script.WriteLine();
                }
            } // EvaluateStage

            public bool EvaluateDependencies(StateOfTheWorld stateOfTheWorld) { return true; }
        } // AnnotateRegulatoryRegionsProcessingStage
#else
        class AnnotateRegulatoryRegionsProcessingStage : PerCaseProcessingStage
        {
            public AnnotateRegulatoryRegionsProcessingStage() : base("Annotate Regulatory Regions", "AnnotateRegulatoryRegions.exe", "", getCaseFiles, getOneOffFiles,
                getOutputFiles, 0, 60)
            { }

            static GetCaseFile[] getCaseFiles = { _ => _.tumor_dna_allcount_filename, _ => _.selected_regulatory_maf_filename };
            static GetOneOffFile[] getOneOffFiles = { _ => _.configuration.encodeBEDFile, _ => _.configuration.geneHancerFilename};
            static GetCaseFile[] getOutputFiles = { _ => _.annotated_regulatory_regions_filename, _ => _.annotated_geneHancer_lines_filename };
        } // AnnotateRegulatoryRegionsProcessingStage
#endif

        class RegulatoryMutationsNearMutationsProcessingStage : ProcessingStage
        {
            public RegulatoryMutationsNearMutationsProcessingStage() { }
            public string GetStageName() { return "Regulatory Mutations Near Mutations"; }

            public bool NeedsCases() { return true; }
            public void EvaluateStage(StateOfTheWorld stateOfTheWorld, StreamWriter script, ASETools.RandomizingStreamWriter hpcScript, StreamWriter linuxScript, StreamWriter azureScript, out List<string> filesToDownload, out int nDone, out int nAddedToScript, out int nWaitingForPrerequisites)
            {
                filesToDownload = new List<string>();
                nDone = 0;
                nAddedToScript = 0;
                nWaitingForPrerequisites = 0;

                nDone = stateOfTheWorld.cases.Where(x => x.Value.regulatory_mutations_near_mutations_filename != "").Count();

                if (stateOfTheWorld.selectedGenes == null || !File.Exists(stateOfTheWorld.configuration.geneLocationInformationFilename) || !File.Exists(stateOfTheWorld.configuration.finalResultsDirectory + ASETools.PerGeneASEMapFilename))
                {
                    nWaitingForPrerequisites = 1;
                    return;
                }

                nWaitingForPrerequisites = stateOfTheWorld.cases.Select(x => x.Value).Where(x => x.annotated_regulatory_regions_filename == "" || x.annotated_selected_variants_filename == "").Count();

                int nOnCurrentLine = 0;
                foreach (var case_ in stateOfTheWorld.cases.Select(x => x.Value).Where(x => x.annotated_regulatory_regions_filename != "" && x.annotated_selected_variants_filename != "" && x.regulatory_mutations_near_mutations_filename == ""))
                {
                    if (nOnCurrentLine == 0)
                    {
                        script.Write(stateOfTheWorld.configuration.binariesDirectory + "CisRegulatoryMutationsNearMutations.exe" + stateOfTheWorld.configurationString);
                    }

                    script.Write(" " + case_.case_id);
                    nOnCurrentLine++;

                    if (nOnCurrentLine >= 60)
                    {
                        script.WriteLine();
                        nOnCurrentLine = 0;
                    }

                    nAddedToScript++;
                } // foreach case

                if (nOnCurrentLine != 0)
                {
                    script.WriteLine();
                }

            }// EvaluateStage

            public bool EvaluateDependencies(StateOfTheWorld stateOfTheWorld) { return true; }
        } // RegulatoryMutationsNearMutationsProcessingStage

        class RegulatoryMutationsByVAFProcessingStage : ProcessingStage
        {
            public RegulatoryMutationsByVAFProcessingStage() { }
            public string GetStageName() { return "Regulatory Mutations by VAF"; }

            public bool NeedsCases() { return true; }

            public bool EvaluateDependencies(StateOfTheWorld stateOfTheWorld) { return true; }

            public void EvaluateStage(StateOfTheWorld stateOfTheWorld, StreamWriter script, ASETools.RandomizingStreamWriter hpcScript, StreamWriter linuxScript, StreamWriter azureScript, out List<string> filesToDownload, out int nDone, out int nAddedToScript, out int nWaitingForPrerequisites)
            {
                filesToDownload = new List<string>();
                nDone = 0;
                nAddedToScript = 0;
                nWaitingForPrerequisites = 0;

                if (File.Exists(stateOfTheWorld.configuration.finalResultsDirectory + ASETools.cisRegulatoryMutationsByVAFFilename))
                {
                    nDone = 1;
                    return;
                }

                if (stateOfTheWorld.cases.Any(x => x.Value.regulatory_mutations_near_mutations_filename == ""))
                {
                    nWaitingForPrerequisites = 1;
                    return;
                }

                script.WriteLine(stateOfTheWorld.configuration.binariesDirectory + "CisRegulatoryMutationsByMutationCount.exe" + stateOfTheWorld.configurationString);  // This used to work differently, hence the non-descriptive name.
                nAddedToScript = 1;
            } // EvaluateStage
        } // RegulatoryMutationsByVAFProcessingStage

        class IndexBAMsProcessingStage : ProcessingStage
        {
            public IndexBAMsProcessingStage() { }
            public string GetStageName() { return "Index BAMs"; }

            public bool NeedsCases() { return true; }

            public bool EvaluateDependencies(StateOfTheWorld stateOfTheWorld) { return true; }

            public void EvaluateStage(StateOfTheWorld stateOfTheWorld, StreamWriter script, ASETools.RandomizingStreamWriter hpcScript, StreamWriter linuxScript, StreamWriter azureScript, out List<string> filesToDownload, out int nDone, out int nAddedToScript, out int nWaitingForPrerequisites)
            {
                filesToDownload = new List<string>();
                nDone = 0;
                nAddedToScript = 0;
                nWaitingForPrerequisites = 0;

                if (!stateOfTheWorld.configuration.isBeatAML)
                {
                    return; // The gdc files are all already indexed, so this is handled by the download stage
                }

                foreach (var case_ in stateOfTheWorld.cases.Select(x => x.Value))
                {
                    HandleFile(case_.normal_dna_filename, script, ref nAddedToScript, ref nDone, ref nWaitingForPrerequisites);
                    HandleFile(case_.tumor_dna_filename, script, ref nAddedToScript, ref nDone, ref nWaitingForPrerequisites);
                    HandleFile(case_.normal_rna_filename, script, ref nAddedToScript, ref nDone, ref nWaitingForPrerequisites);
                    HandleFile(case_.tumor_rna_filename, script, ref nAddedToScript, ref nDone, ref nWaitingForPrerequisites);
                }
            } // EvaluateStage

            void HandleFile(string filename, StreamWriter script, ref int nAddedToScript, ref int nDone, ref int nWaitingForPrerequisites)
            {
                if (filename == "")
                {
                    return;
                }

                if (filename.EndsWith(".unsorted-bam"))
                {
                    nWaitingForPrerequisites++;
                    return;
                }

                if (!filename.EndsWith(".bam"))
                {
                    Console.WriteLine("BAM file doesn't end with .bam!: " + filename);
                    return;
                }

                var baseFilename = filename.Substring(0, filename.Length - 4);

                var baiFileName = baseFilename + ".bai";
                if (File.Exists(baiFileName))
                {
                    nDone++;
                    return;
                } 

                if (!File.Exists(baseFilename + ".unsorted-bam"))
                {
                    //
                    // Needs to be sorted first.
                    //
                    nWaitingForPrerequisites++;
                    return;
                }
           
                script.WriteLine("samtools index " + filename + " " + baiFileName);
                nAddedToScript++;

            }
        } // IndexBAMsProcessingStage

        class SortBAMsProcessingStage : ProcessingStage
        {
            public SortBAMsProcessingStage() { }
            public string GetStageName() { return "Sort BAMs"; }

            public bool NeedsCases() { return true; }

            public bool EvaluateDependencies(StateOfTheWorld stateOfTheWorld) { return true; }

            public void EvaluateStage(StateOfTheWorld stateOfTheWorld, StreamWriter script, ASETools.RandomizingStreamWriter hpcScript, StreamWriter linuxScript, StreamWriter azureScript, out List<string> filesToDownload, out int nDone, out int nAddedToScript, out int nWaitingForPrerequisites)
            {
                filesToDownload = new List<string>();
                nDone = 0;
                nAddedToScript = 0;
                nWaitingForPrerequisites = 0;

                if (!stateOfTheWorld.configuration.isBeatAML)
                {
                    return; // The gdc files are all already indexed, so this is handled by the download stage
                }

                foreach (var case_ in stateOfTheWorld.cases.Select(x => x.Value))
                {
                    HandleFile(case_.normal_dna_filename, script, ref nAddedToScript, ref nDone, ref nWaitingForPrerequisites);
                    HandleFile(case_.tumor_dna_filename, script, ref nAddedToScript, ref nDone, ref nWaitingForPrerequisites);
                    HandleFile(case_.normal_rna_filename, script, ref nAddedToScript, ref nDone, ref nWaitingForPrerequisites);
                    HandleFile(case_.tumor_rna_filename, script, ref nAddedToScript, ref nDone, ref nWaitingForPrerequisites);
                }
            } // EvaluateStage

            void HandleFile(string filename, StreamWriter script, ref int nAddedToScript, ref int nDone, ref int nWaitingForPrerequisites)
            {
                if (filename == "")
                {
                    return;
                }

                if (filename.EndsWith(".unsorted-bam"))
                {
                    // This is the weird case where the rename ran but the sort didn't.  Just generate the sort command.
                    var bamFileName = filename.Substring(0, filename.Length - 13) + ".bai";

                    script.WriteLine("samtools sort " + filename + " " + bamFileName);
                    nAddedToScript++;
                    return;
                }

                if (!filename.EndsWith(".bam"))
                {
                    Console.WriteLine("BAM file doesn't end with .bam!: " + filename);
                    return;
                }

                var baseFilename = filename.Substring(0, filename.Length - 4);

                var baiFileName = baseFilename + ".bai";
                if (File.Exists(baiFileName) || File.Exists(baseFilename + ".unsorted-bam"))
                {
                    nDone++;
                    return;
                }
                
                script.WriteLine("mv " + filename + " " + baseFilename + ".unsorted-bam");
                script.WriteLine("samtools sort " + baseFilename + ".unsorted-bam " + filename);
                nAddedToScript++;

            } 
        } // SortBAMsProcessingStage

        class VAFHistogramsProcessingStage : ProcessingStage
        {
            public VAFHistogramsProcessingStage() { }
            public string GetStageName() { return "VAF Histograms"; }

            public bool NeedsCases() { return true; }

            public bool EvaluateDependencies(StateOfTheWorld stateOfTheWorld) { return true; }

            public void EvaluateStage(StateOfTheWorld stateOfTheWorld, StreamWriter script, ASETools.RandomizingStreamWriter hpcScript, StreamWriter linuxScript, StreamWriter azureScript, out List<string> filesToDownload, out int nDone, out int nAddedToScript, out int nWaitingForPrerequisites)
            {
                filesToDownload = new List<string>();
                nDone = 0;
                nAddedToScript = 0;
                nWaitingForPrerequisites = 0;

                if (File.Exists(stateOfTheWorld.configuration.finalResultsDirectory + ASETools.vaf_histogram_filename) )
                {
                    nDone = 1;
                    return;
                }

                if (!File.Exists(stateOfTheWorld.configuration.finalResultsDirectory + ASETools.PerGeneASEMapFilename) || stateOfTheWorld.cases.Any(x => x.Value.annotated_selected_variants_filename == "")  || !File.Exists(stateOfTheWorld.configuration.redundantChromosomeRegionFilename))
                {
                    nWaitingForPrerequisites = 1;
                    return;
                }

                script.WriteLine(stateOfTheWorld.configuration.binariesDirectory + "VAFDistribution.exe" + stateOfTheWorld.configurationString);
                nAddedToScript = 1;

            } // EvaluateStage
        } // VAFHistogramsProcessingStage

        class ASEScatterProcessingStage : ProcessingStage
        {
            public ASEScatterProcessingStage() { }
            public string GetStageName() { return "ASE Scatter"; }

            public bool NeedsCases() { return true; }

            public bool EvaluateDependencies(StateOfTheWorld stateOfTheWorld) { return true; }

            public void EvaluateStage(StateOfTheWorld stateOfTheWorld, StreamWriter script, ASETools.RandomizingStreamWriter hpcScript, StreamWriter linuxScript, StreamWriter azureScript, out List<string> filesToDownload, out int nDone, out int nAddedToScript, out int nWaitingForPrerequisites)
            {
                filesToDownload = new List<string>();
                nDone = 0;
                nAddedToScript = 0;
                nWaitingForPrerequisites = 0;

                if (File.Exists(stateOfTheWorld.configuration.finalResultsDirectory + ASETools.Configuration.GlobalScatterGraphFilename))
                {
                    nDone = 1;
                    return;
                }

                if (stateOfTheWorld.cases.Any(x => x.Value.annotated_selected_variants_filename == "") || !File.Exists(stateOfTheWorld.configuration.finalResultsDirectory + ASETools.ASECorrectionFilename))
                {
                    nWaitingForPrerequisites = 1;
                    return;
                }

                script.WriteLine(stateOfTheWorld.configuration.binariesDirectory + "ASEScatter.exe" + stateOfTheWorld.configurationString);
                nAddedToScript = 1;

            } // EvaluateStage
        } // ASEScatterProcessingStage

        class ExpressionByGeneProcessingStage : ProcessingStage
        {
            public ExpressionByGeneProcessingStage() { }
            public string GetStageName() { return "Expression by gene"; }

            public bool NeedsCases() { return true; }

            public bool EvaluateDependencies(StateOfTheWorld stateOfTheWorld) { return true; }

            public void EvaluateStage(StateOfTheWorld stateOfTheWorld, StreamWriter script, ASETools.RandomizingStreamWriter hpcScript, StreamWriter linuxScript, StreamWriter azureScript, out List<string> filesToDownload, out int nDone, out int nAddedToScript, out int nWaitingForPrerequisites)
            {
                filesToDownload = new List<string>();
                nDone = 0;
                nAddedToScript = 0;
                nWaitingForPrerequisites = 0;

                foreach (var disease in stateOfTheWorld.diseases)
                {
                    var casesForThisDisease = stateOfTheWorld.cases.Select(x => x.Value).Where(x => x.disease() == disease).ToList();
                    if (casesForThisDisease.All(x => x.expression_by_gene_filename != ""))
                    {
                        nDone++;
                        continue;
                    }

                    if (!stateOfTheWorld.expressionFiles.ContainsKey(disease) ||  casesForThisDisease.Any(x => x.tumor_rna_allcount_filename == "" || x.tumor_rna_mapped_base_count_filename == "") ||
                        !File.Exists(stateOfTheWorld.configuration.basesInKnownCodingRegionsDirectory + ASETools.basesInKnownCodingRegionsPrefix + disease + ".txt"))
                    {
                        nWaitingForPrerequisites++;
                        continue;
                    }

                    script.WriteLine(stateOfTheWorld.configuration.binariesDirectory + "ExpressionByGene.exe " + stateOfTheWorld.configurationString + disease);
                    nAddedToScript++;
                }

            } // EvaluateStage 
        } // ExpressionByGeneProcessingStage

        class BasesInKnownCodingRegionsProcessingStage : ProcessingStage
        {
            public BasesInKnownCodingRegionsProcessingStage() { }
            public string GetStageName() { return "Compute bases in known coding regions"; }

            public bool NeedsCases() { return true; }

            public bool EvaluateDependencies(StateOfTheWorld stateOfTheWorld) { return true; }

            public void EvaluateStage(StateOfTheWorld stateOfTheWorld, StreamWriter script, ASETools.RandomizingStreamWriter hpcScript, StreamWriter linuxScript, StreamWriter azureScript, out List<string> filesToDownload, out int nDone, out int nAddedToScript, out int nWaitingForPrerequisites)
            {
                filesToDownload = new List<string>();
                nDone = 0;
                nAddedToScript = 0;
                nWaitingForPrerequisites = 0;

                if (!File.Exists(stateOfTheWorld.configuration.finalResultsDirectory + ASETools.ASECorrectionFilename))
                {
                    nWaitingForPrerequisites = 1;
                    return;
                }

                foreach (var disease in stateOfTheWorld.diseases)
                {
                    if (File.Exists(stateOfTheWorld.configuration.basesInKnownCodingRegionsDirectory + ASETools.basesInKnownCodingRegionsPrefix + disease + ".txt"))
                    {
                        nDone++;
                        continue;
                    }

                    if (!stateOfTheWorld.expressionFiles.ContainsKey(disease) || stateOfTheWorld.selectedGenes == null)
                    {
                        nWaitingForPrerequisites++;
                        continue;
                    }

                    script.WriteLine(stateOfTheWorld.configuration.binariesDirectory + "ComputeBasesInCodingAndKnownExpressionRegions.exe " + stateOfTheWorld.configurationString + disease);
                    nAddedToScript++;
                }

            } // EvaluateStage
        } // BasesInKnownCodingRegionsProcessingStage

        class OverallGeneExpressionProcessingStage : ProcessingStage
        {
            public OverallGeneExpressionProcessingStage() { }
            public string GetStageName() { return "Overall gene expression"; }

            public bool NeedsCases() { return true; }

            public bool EvaluateDependencies(StateOfTheWorld stateOfTheWorld) { return true; }

            public void EvaluateStage(StateOfTheWorld stateOfTheWorld, StreamWriter script, ASETools.RandomizingStreamWriter hpcScript, StreamWriter linuxScript, StreamWriter azureScript, out List<string> filesToDownload, out int nDone, out int nAddedToScript, out int nWaitingForPrerequisites)
            {
                filesToDownload = new List<string>();
                nDone = 0;
                nAddedToScript = 0;
                nWaitingForPrerequisites = 0;

                if (File.Exists(stateOfTheWorld.configuration.finalResultsDirectory + ASETools.Configuration.PerGeneExpressionHistogramsFilename))
                {
                    nDone = 1;
                    return;
                }

                if (stateOfTheWorld.cases.Select(x => x.Value).Any(x => x.annotated_selected_variants_filename == "" || x.expression_by_gene_filename == "") ||
                    !File.Exists(stateOfTheWorld.configuration.finalResultsDirectory + ASETools.ASECorrectionFilename))
                {
                    nWaitingForPrerequisites = 1;
                    return;
                }

                script.WriteLine(stateOfTheWorld.configuration.binariesDirectory + "OverallGeneExpressionByMutationCount.exe" + stateOfTheWorld.configurationString);
                nAddedToScript = 1;

            } // EvaluateStage
        } // OverallGeneExpressionProcessingStage

        class GenerateTranscriptomeReadsAndReferenceProcessingStage : ProcessingStage
        {
            public GenerateTranscriptomeReadsAndReferenceProcessingStage() { }
            public string GetStageName() { return "Generate Transcriptome Reads and Reference"; }

            public bool NeedsCases() { return false; }

            public bool EvaluateDependencies(StateOfTheWorld stateOfTheWorld) { return true; }

            public void EvaluateStage(StateOfTheWorld stateOfTheWorld, StreamWriter script, ASETools.RandomizingStreamWriter hpcScript, StreamWriter linuxScript, StreamWriter azureScript, out List<string> filesToDownload, out int nDone, out int nAddedToScript, out int nWaitingForPrerequisites)
            {
                filesToDownload = new List<string>();
                nDone = 0;
                nAddedToScript = 0;
                nWaitingForPrerequisites = 0;
                bool needFasta = !File.Exists(stateOfTheWorld.configuration.chromosomeMapsDirectory + ASETools.transcriptomeFastaFilename);

                for (int i = 1; i <= ASETools.nHumanAutosomes; i++)
                {
                    if (File.Exists(stateOfTheWorld.configuration.chromosomeMapsDirectory + "transcriptome_" + i + ASETools.allLociExtension))
                    {
                        nDone++;
                        continue;
                    }

                    script.Write(stateOfTheWorld.configuration.binariesDirectory + "GenerateReadsForRepetitiveTranscriptome.exe" + stateOfTheWorld.configurationString + " " + stateOfTheWorld.configuration.localIndexDirectory + " " +
                                 stateOfTheWorld.configuration.chromosomeMapsDirectory + "transcriptome_" + i + ASETools.allLociExtension + " " + i);
                    if (needFasta)
                    {
                        script.WriteLine(" " + stateOfTheWorld.configuration.chromosomeMapsDirectory + ASETools.transcriptomeFastaFilename);
                        needFasta = false;
                    } else
                    {
                        script.WriteLine();
                    }

                    nAddedToScript++;
                }

                if (needFasta)
                {
                    script.WriteLine(stateOfTheWorld.configuration.binariesDirectory + "GenerateReadsForRepetitiveTranscriptome.exe" + stateOfTheWorld.configurationString + " " + stateOfTheWorld.configuration.localIndexDirectory + " " +
                                 stateOfTheWorld.configuration.chromosomeMapsDirectory + "transcriptome" + ASETools.allLociExtension + " " + stateOfTheWorld.configuration.chromosomeMapsDirectory + " 22 " +
                                 stateOfTheWorld.configuration.chromosomeMapsDirectory + ASETools.transcriptomeFastaFilename);
                    nAddedToScript++;
                }

            } // EvaluateStage
        } // GenerateTranscriptomeReadsAndReferenceProcessingStage

        class GenerateTranscriptomeIndexProcessingStage : ProcessingStage
        {
            public GenerateTranscriptomeIndexProcessingStage() { }
            public string GetStageName() { return "Generate Transcriptome Index"; }

            public bool NeedsCases() { return false; }

            public bool EvaluateDependencies(StateOfTheWorld stateOfTheWorld) { return true; }

            public void EvaluateStage(StateOfTheWorld stateOfTheWorld, StreamWriter script, ASETools.RandomizingStreamWriter hpcScript, StreamWriter linuxScript, StreamWriter azureScript, out List<string> filesToDownload, out int nDone, out int nAddedToScript, out int nWaitingForPrerequisites)
            {
                filesToDownload = new List<string>();
                nDone = 0;
                nAddedToScript = 0;
                nWaitingForPrerequisites = 0;

                if (File.Exists(stateOfTheWorld.configuration.chromosomeMapsDirectory + ASETools.generatedTranscriptomeIndexName + "\\GenomeIndexHash"))
                {
                    nDone++;
                    return;
                } else if (!File.Exists(stateOfTheWorld.configuration.chromosomeMapsDirectory + ASETools.transcriptomeFastaFilename))
                {
                    nWaitingForPrerequisites++;
                    return;
                } else
                {
                    script.WriteLine(stateOfTheWorld.configuration.binariesDirectory + "snap.exe index " + stateOfTheWorld.configuration.chromosomeMapsDirectory + ASETools.transcriptomeFastaFilename + " " +
                        stateOfTheWorld.configuration.chromosomeMapsDirectory + ASETools.generatedTranscriptomeIndexName + " -s 16");
                    nAddedToScript++;
                }
             } // EvaluateStage
        } // GenerateTranscriptomeIndexProcessingStage


        class AnnotateScatterGraphsProcessingStage : ProcessingStage
        {
            public AnnotateScatterGraphsProcessingStage() { }
            public string GetStageName() { return "Annotate Scatter Graphs"; }

            public bool NeedsCases() { return true; }

            public bool EvaluateDependencies(StateOfTheWorld stateOfTheWorld) { return true; }

            public void EvaluateStage(StateOfTheWorld stateOfTheWorld, StreamWriter script, ASETools.RandomizingStreamWriter hpcScript, StreamWriter linuxScript, StreamWriter azureScript, out List<string> filesToDownload, out int nDone, out int nAddedToScript, out int nWaitingForPrerequisites)
            {
                filesToDownload = new List<string>();
                nDone = 0;
                nAddedToScript = 0;
                nWaitingForPrerequisites = 0;

                if (File.Exists(stateOfTheWorld.configuration.geneScatterGraphsDirectory + ASETools.annotated_scatter_graphs_histogram_filename))
                {
                    nDone = 1;
                    return;
                }

                if (!File.Exists(stateOfTheWorld.configuration.geneScatterGraphsDirectory + ASETools.scatterGraphsSummaryFilename) || 
                    stateOfTheWorld.diseases.Any(disease => ASETools.chromosomes.Any(chromosome => 
                        !File.Exists(stateOfTheWorld.configuration.geneScatterGraphsLinesWithPercentilesDirectory + ASETools.GeneScatterGraphLinesWithPercentilesPrefix + disease + "_" + ASETools.chromosomeNameToNonChrForm(chromosome)))))
                {
                    nWaitingForPrerequisites = 1;
                    return;
                }

                script.WriteLine(stateOfTheWorld.configuration.binariesDirectory + "AnnotateScatterGraphs.exe" + stateOfTheWorld.configurationString);
                nAddedToScript = 1;

            } // EvaluateStage
        } // AnnotateScatterGraphsProcessingStage

        class ReadLengthDistributionProcessingStage : ProcessingStage
        {
            public ReadLengthDistributionProcessingStage() { }
            public string GetStageName() { return "Read length distribution"; }

            public bool NeedsCases() { return true; }

            public bool EvaluateDependencies(StateOfTheWorld stateOfTheWorld) { return true; }

            public void EvaluateStage(StateOfTheWorld stateOfTheWorld, StreamWriter script, ASETools.RandomizingStreamWriter hpcScript, StreamWriter linuxScript, StreamWriter azureScript, out List<string> filesToDownload, out int nDone, out int nAddedToScript, out int nWaitingForPrerequisites)
            {
                filesToDownload = new List<string>();
                nDone = 0;
                nAddedToScript = 0;
                nWaitingForPrerequisites = 0;

                if (File.Exists(stateOfTheWorld.configuration.finalResultsDirectory + ASETools.ReadLengthHistogramFilename))
                {
                    nDone = 1;
                    return;
                }

                if (!File.Exists(stateOfTheWorld.configuration.finalResultsDirectory + ASETools.ASECorrectionFilename))
                {
                    nWaitingForPrerequisites = 1;
                    return;
                }

                if (stateOfTheWorld.cases.Select(_ => _.Value).Any(_ => _.normal_dna_reads_at_tentative_selected_variants_filename == "" || _.tumor_dna_reads_at_tentative_selected_variants_filename == "" || _.tumor_rna_reads_at_tentative_selected_variants_filename == "" ||
                                                          _.tentative_selected_variants_filename == ""))
                {
                    nWaitingForPrerequisites = 1;
                    return;
                }

                script.WriteLine(stateOfTheWorld.configuration.binariesDirectory + "ReadLengthDistributions.exe" + stateOfTheWorld.configurationString);
                nAddedToScript = 1;

            } // EvaluateStage
        } // ReadLengthDistributionProcessingStage

        class ExpressionDecilesByChromosomeProcessingStage : PerChromosomePerDiseaseProcessingStage
        {
            public ExpressionDecilesByChromosomeProcessingStage() : base ("Expression Deciles by Chromosome", "ComputeExpressionDistribution.exe", true, "", getPerCaseInputs, null, null, getOutputs)
            { }

            static GetCaseFile[] getPerCaseInputs = { _ => _.tumor_rna_allcount_filename , _ => _.tumor_rna_mapped_base_count_filename};
            static GetPerChromosomePerDiseaseFile[] getOutputs = { (stateOfTheWorld, chromosome, disease) => getOutput(stateOfTheWorld, chromosome, disease) };

            static string getOutput(StateOfTheWorld stateOfTheWorld, string chromosome, string disease)
            {
                if (stateOfTheWorld.expressionDistributionByChromosomeMap.map[disease].ContainsKey(ASETools.chromosomeNameToNonChrForm(chromosome)))
                {
                    return stateOfTheWorld.expressionDistributionByChromosomeMap.map[disease][ASETools.chromosomeNameToNonChrForm(chromosome)];
                }

                return "";
            }

        } // ExpressionDecilesByChromosomeProcessingStage

        class ChooseAnnotatedVariantsProcessingStage : ProcessingStage
        {
            public ChooseAnnotatedVariantsProcessingStage() { }
            public string GetStageName() { return "Choose Annotated Variants"; }

            public bool NeedsCases() { return true; }

            public bool EvaluateDependencies(StateOfTheWorld stateOfTheWorld) { return true; }

            public void EvaluateStage(StateOfTheWorld stateOfTheWorld, StreamWriter script, ASETools.RandomizingStreamWriter hpcScript, StreamWriter linuxScript, StreamWriter azureScript, out List<string> filesToDownload, out int nDone, out int nAddedToScript, out int nWaitingForPrerequisites)
            {
                filesToDownload = new List<string>();
                nDone = 0;
                nAddedToScript = 0;
                nWaitingForPrerequisites = 0;

                if (stateOfTheWorld.cases.All(_ => _.Value.annotated_selected_variants_filename != ""))
                {
                    nDone = 1;
                    return;
                }

                if (stateOfTheWorld.cases.Any(_ => _.Value.tentative_annotated_selected_variants_filename == ""))
                {
                    nWaitingForPrerequisites = stateOfTheWorld.cases.Where(_ => _.Value.tentative_annotated_selected_variants_filename == "").Count();  // This is a little funny, since there's only one global run, but it's more informative.
                    return;
                }

                script.WriteLine(stateOfTheWorld.configuration.binariesDirectory + "ChooseAnnotatedVariants.exe" + stateOfTheWorld.configurationString);
                nAddedToScript = 1;

            } // EvaluateStage
        } // ChooseAnnotatedVariantsProcessingStage

        class CaseMetadataProcessingStage : PerCaseProcessingStage
        {
            public CaseMetadataProcessingStage(): base("Compute Case Metadata", "MakeCaseMetadata.exe", "", getInputFiles, null, getOutputFile)
            { }

            static GetCaseFile[] getInputFiles = { _ => _.normal_dna_filename, _ => _.tumor_dna_filename, _ => _.tumor_rna_filename, _ => (_.normal_rna_file_id == "") ? _.tumor_dna_filename : _.normal_rna_filename,
                                                   _ => _.normal_dna_reads_at_tentative_selected_variants_filename, _ => _.tumor_dna_reads_at_tentative_selected_variants_filename, _ => _.tumor_rna_reads_at_tentative_selected_variants_filename,
                                                   _ => (_.normal_rna_file_id == "") ? _.tumor_dna_reads_at_tentative_selected_variants_filename : _.normal_rna_reads_at_tentative_selected_variants_filename};

            static GetCaseFile[] getOutputFile = { _ => _.case_metadata_filename };
        } // CaseMetadataProcessingStage

        class ConsolodatedCaseMetadataProcessingStage : SingleOutputProcessingStage
        {
            public ConsolodatedCaseMetadataProcessingStage() : base("Consolodate Case Metadata", true, "CreateConsolodatedCaseMetadata.exe", "", getInputFiles, null, getOutputFile) { }

            static GetCaseFile[] getInputFiles = { _ => _.case_metadata_filename };
            static GetOneOffFile[] getOutputFile = { _ => _.configuration.finalResultsDirectory + ASETools.ConsolodatedCaseMetadataFilename };
        } // ConsolodatedCaseMetadataProcessingStage

        class UniparentalDisomyProcessingStage : SingleOutputProcessingStage
        {
            public UniparentalDisomyProcessingStage() : base("Uniparental Disomy", true, "UniparentalDisomy.exe", "", getInputFiles, null, getOutputFile)
            { }

            static GetCaseFile[] getInputFiles = { _ => _.tentative_asv_without_cnvs_filename };
            static GetOneOffFile[] getOutputFile = { _ => _.configuration.finalResultsDirectory + ASETools.UniparentalDisomyFilename };
        } // UniparentalDisomyProcessingStage

        class FPKMProcessingStage : ProcessingStage
		{
			public FPKMProcessingStage() { }

			public string GetStageName() { return "Process FPKM Data"; }

			public bool NeedsCases() { return true; }

			public void EvaluateStage(StateOfTheWorld stateOfTheWorld, StreamWriter script, ASETools.RandomizingStreamWriter hpcScript, StreamWriter linuxScript, StreamWriter azureScript, out List<string> filesToDownload, out int nDone, out int nAddedToScript, out int nWaitingForPrerequisites)
			{
				filesToDownload = new List<string>();
				nDone = 0;
				nAddedToScript = 0;
				nWaitingForPrerequisites = 0;

				foreach (var caseEntry in stateOfTheWorld.cases)
				{
					var case_ = caseEntry.Value;

					if (case_.normal_fpkm_filename == "" && case_.normal_fpkm_file_id != "")
					{
						nWaitingForPrerequisites = 1;
						filesToDownload.Add(case_.normal_fpkm_file_id);
					}
					
					if (case_.tumor_fpkm_filename == "" && case_.tumor_fpkm_file_id != "")
					{
						nWaitingForPrerequisites = 1;
						filesToDownload.Add(case_.tumor_fpkm_file_id);
					}

					// nAddedToScript = 1; // Commented out until there's something to add to the script.
				}

			} // EvaluateStage

			public bool EvaluateDependencies(StateOfTheWorld stateOfTheWorld)
			{
				DateTime oldestFile;
				if (stateOfTheWorld.scatterGraphsSummaryFile == "")
				{
					return true;
				}

				oldestFile = new FileInfo(stateOfTheWorld.scatterGraphsSummaryFile).LastWriteTime;
				foreach (var selectedGene in stateOfTheWorld.selectedGenes)
				{
					if (!stateOfTheWorld.scatterGraphsByHugoSymbol.ContainsKey(selectedGene.Hugo_Symbol))
					{
						return true;
					}

					var date = new FileInfo(stateOfTheWorld.scatterGraphsByHugoSymbol[selectedGene.Hugo_Symbol]).LastWriteTime;
					if (date < oldestFile)
					{
						oldestFile = date;
					}
				}

				//
				// Now look at the dependencies.
				//
				foreach (var caseEntry in stateOfTheWorld.cases)
				{
					var case_ = caseEntry.Value;

					if (case_.annotated_selected_variants_filename == "" || new FileInfo(case_.annotated_selected_variants_filename).LastWriteTime > oldestFile)
					{
						Console.WriteLine("Case " + case_.case_id + " either doesn't have an annotated selected variants file, or it is newer than a gene scatter graph file that depends on it.");
						return false;
					}

					if (case_.tumor_dna_mapped_base_count_filename == "" || new FileInfo(case_.tumor_dna_mapped_base_count_filename).LastWriteTime > oldestFile ||
						case_.tumor_rna_mapped_base_count_filename == "" || new FileInfo(case_.tumor_rna_mapped_base_count_filename).LastWriteTime > oldestFile)
					{
						Console.WriteLine("Case " + case_.case_id + " either doesn't have a tumor mapped base count file, or it is newer than a gene scatter graph file that depends on it.");
						return false;
					}
				}

				return true;
			} // EvaluateDependencies

		} // FPKMProcessingStage

        class DistanceBetweenMutationsProcessingStage : SingleOutputProcessingStage
        {
            public DistanceBetweenMutationsProcessingStage() : base("Distance Between Mutations", true, "DistanceBetweenMutations.exe", "", null, getInputFile, getOutputFile)
            { }

            static GetOneOffFile[] getInputFile = { stateOfTheWorld => stateOfTheWorld.scatterGraphsSummaryFile, _ => _.configuration.finalResultsDirectory + ASETools.ASECorrectionFilename };
            static GetOneOffFile[] getOutputFile = { stateOfTheWorld => stateOfTheWorld.configuration.finalResultsDirectory + ASETools.DistanceBetweenMutationsFilename };
        } // DistanceBetweenMutationsProcessingStage

        class AddPercentilesToScatterGraphLinesProcessingStage : PerChromosomePerDiseaseProcessingStage
        {
            public AddPercentilesToScatterGraphLinesProcessingStage() : base("Add Percentiles to Scatter Graphs", "AddPercentilesToScatterGraphLines.exe", true, "", getPerCaseInputs, getPerChromosomePerDiseaseInputs, getOneOffInputs, getOutputs)
            { }

            static GetCaseFile[] getPerCaseInputs = { _ => _.tumor_rna_mapped_base_count_filename };
            static GetPerChromosomePerDiseaseFile[] getPerChromosomePerDiseaseInputs = { (stateOfTheWorld, chromosome, disease) => getExpressionDistributionInput(stateOfTheWorld, chromosome, disease)  };
            static GetOneOffFile[] getOneOffInputs = { _ => _.configuration.geneScatterGraphsDirectory + ASETools.scatterGraphsSummaryFilename };
            static GetPerChromosomePerDiseaseFile[] getOutputs = { (stateOfTheWorld, chromosome, disease) => stateOfTheWorld.configuration.geneScatterGraphsLinesWithPercentilesDirectory + ASETools.GeneScatterGraphLinesWithPercentilesPrefix + disease + "_" + ASETools.chromosomeNameToNonChrForm(chromosome) };

            static string getExpressionDistributionInput(StateOfTheWorld stateOfTheWorld, string chromosome, string disease)
            {
                if (!stateOfTheWorld.expressionDistributionByChromosomeMap.map[disease].ContainsKey(ASETools.chromosomeNameToNonChrForm(chromosome)))
                {
                    return "";
                }
                return stateOfTheWorld.expressionDistributionByChromosomeMap.map[disease][ASETools.chromosomeNameToNonChrForm(chromosome)];
            }
        } // AddPercentilesToScatterGraphLinesProcessingStage

        class SummarizePhasingProcessingStage : SingleOutputProcessingStage
        {
            public SummarizePhasingProcessingStage() : base("Summarize Phasing", true, "SummarizePhasing.exe", "", getPerCaseFile, getOneOffInput, getOutputFile) { }

            static GetOneOffFile[] getOneOffInput = { _ => _.configuration.finalResultsDirectory + ASETools.ASECorrectionFilename };
            static GetCaseFile[] getPerCaseFile = { _ => _.variant_phasing_filename };
            static GetOneOffFile[] getOutputFile = { _ => _.configuration.finalResultsDirectory + ASETools.PhasingForNearbyVariantsFilename };
        }

        class ReadStaticticsProcessingStage : PerCaseProcessingStage
        {
            public ReadStaticticsProcessingStage() : base("Read Statictics", "ComputeReadStatistics.exe", "", perCaseFiles, null, getOutputFile) { }

            static GetCaseFile[] perCaseFiles = { _ => _.tumor_dna_filename, _ => _.tumor_rna_filename, _ => _.normal_dna_filename, _ => (_.normal_rna_file_id == "") ? _.tumor_dna_filename : _.normal_rna_filename };
            static GetCaseFile[] getOutputFile = { _ => _.read_statictics_filename };
        }

        class SummarizeCaseMetadataProcessingStage : SingleOutputProcessingStage
        {
            public SummarizeCaseMetadataProcessingStage() : base("Summarize Case Metadata", true, "SummarizeCaseMetadata.exe", "", getInputFile, null, getOutputFile) { }

            static GetOneOffFile[] getOutputFile = { _ => _.configuration.finalResultsDirectory + ASETools.CaseMetadataSummaryFilename };
            static GetCaseFile[] getInputFile = { _ => _.case_metadata_filename };
        }

        //
        // This represents the state of the world.  Processing stages look at this state and generate actions to move us along.
        //
        class StateOfTheWorld
        {
            public StateOfTheWorld(ASETools.Configuration configuration_) 
            {
                configuration = configuration_;
                if (configuration.configurationFileLocationExplicitlySpecified)
                {
                    configurationString = " -configuration " + configuration.configuationFilePathname + " ";
                }
            }

            public ASETools.Configuration configuration;
            public Dictionary<string, ASETools.DownloadedFile> downloadedFiles = null;
            public Dictionary<string, List<ASETools.DerivedFile>> derivedFiles = null;
            public Dictionary<string, FileInfo> expressionFiles = null;            
            public Dictionary<string, ASETools.MAFInfo> mafInfo = null;
            public Dictionary<string, ASETools.Case> cases = null;
            public List<ASETools.Case> listOfCases = null;
            public List<string> diseases = null;
            public Dictionary<string, string> fileIdToCaseId = null;
            public Dictionary<string, long> fileSizesFromGDC = null;
            public List<ASETools.SelectedGene> selectedGenes = null;
            public string scatterGraphsSummaryFile = "";
            public Dictionary<string, string> scatterGraphsByHugoSymbol = new Dictionary<string, string>();
            public ASETools.ExpressionDistributionByChromosomeMap expressionDistributionByChromosomeMap = new ASETools.ExpressionDistributionByChromosomeMap();
            public ASETools.CommonData commonData;
            Dictionary<string, ASETools.ScannedFilesystem> fileSystems = null;
            public ASETools.BiasedRandom<string> randomFilesystemByFreeSpace = null;
            public Dictionary<string, ASETools.CaseMetadata> caseMetadata = null;

            bool commonDataAvailable = false;

            public bool hasCommonData() { return commonDataAvailable; }

            public readonly string configurationString = "";

            public void DetermineTheStateOfTheWorld(string [] args)
            {
                commonData = ASETools.CommonData.LoadCommonData(args);

                commonDataAvailable = commonData != null && commonData.cases != null && commonData.configuration != null && commonData.geneLocationInformation != null && commonData.geneMap != null && commonData.perGeneASEMap != null &&
                                      commonData.aseCorrection != null && commonData.expressionDistributionByChromosomeMap != null && commonData.diseases != null && commonData.clinicalSummariesByPatientId != null;

                
                ASETools.ScanFilesystems(configuration, out downloadedFiles, out derivedFiles, out fileSystems);

                randomFilesystemByFreeSpace = new ASETools.BiasedRandom<string>(fileSystems.Select(_ => new KeyValuePair<string, ulong>(_.Key, _.Value.totalFreeBytes)).ToList());

                if (File.Exists(configuration.selectedGenesFilename))
                {
                    selectedGenes = ASETools.SelectedGene.LoadFromFile(configuration.selectedGenesFilename);

                    foreach (var selectedGene in selectedGenes)
                    {
                        if (File.Exists(configuration.geneScatterGraphsDirectory + selectedGene.Hugo_Symbol + ".txt"))
                        {
                            scatterGraphsByHugoSymbol.Add(selectedGene.Hugo_Symbol, configuration.geneScatterGraphsDirectory + selectedGene.Hugo_Symbol + ".txt");
                        }
                    }
                }

                if (File.Exists(configuration.geneScatterGraphsDirectory + ASETools.scatterGraphsSummaryFilename))
                {
                    scatterGraphsSummaryFile = configuration.geneScatterGraphsDirectory + ASETools.scatterGraphsSummaryFilename;
                }

                mafInfo = ASETools.MAFInfo.LoadMAFManifest(configuration.mafManifestPathname);
                cases = ASETools.Case.LoadCases(configuration.casesFilePathname);
                listOfCases = (cases == null) ? new List<ASETools.Case>() : cases.Select(_ => _.Value).ToList();

                if (File.Exists(configuration.finalResultsDirectory + ASETools.ConsolodatedCaseMetadataFilename))
                {
                    caseMetadata = ASETools.CaseMetadata.ReadConsolodatedCaseMetadata(configuration.finalResultsDirectory + ASETools.ConsolodatedCaseMetadataFilename);
                }

                fileSizesFromGDC = new Dictionary<string, long>();

                if (null != cases)
                {
                    diseases = new List<string>();

                    foreach (var caseEntry in cases)
                    {
                        var case_ = caseEntry.Value;

                        if (!diseases.Contains(case_.disease()))
                        {
                            diseases.Add(case_.disease());
                        }
                    }

                    foreach (var disease in diseases)
                    {
                        expressionDistributionByChromosomeMap.map.Add(disease, new Dictionary<string, string>());
                    }

                    foreach (var dataDirectory in configuration.dataDirectories)
                    {
                        if (Directory.Exists(dataDirectory))
                        {
                            var expressionDistributionDirectory = dataDirectory + @"..\" + ASETools.ExpressionDistrbutionByChromosomeDirectory;
                            if (!Directory.Exists(expressionDistributionDirectory))
                            {
                                Directory.CreateDirectory(expressionDistributionDirectory);
                            }
                            else
                            {
                                foreach (var file in Directory.GetFiles(expressionDistributionDirectory, ASETools.Expression_distribution_filename_base + "*"))
                                {
                                    var fields = ASETools.GetFileNameFromPathname(file).Substring(ASETools.Expression_distribution_filename_base.Count()).Split('_');
                                    if (fields.Count() != 2)
                                    {
                                        Console.WriteLine("Malformed filename in expression distribution by chromosome directory: " + file);
                                        continue;
                                    }

                                    var disease = fields[1];
                                    var chromosome = fields[0];

                                    if (!diseases.Contains(disease))
                                    {
                                        Console.WriteLine("Expression distribution by chromosome directory contains a file with an unknown disease " + file);
                                        continue;
                                    }

                                    if (expressionDistributionByChromosomeMap.map[disease].ContainsKey(chromosome))
                                    {
                                        Console.WriteLine("Duplicate expression distribution by chromosome files: " + file + " and " + expressionDistributionByChromosomeMap.map[disease][chromosome]);
                                        continue;
                                    }

                                    expressionDistributionByChromosomeMap.map[disease].Add(chromosome, file);
                                } // file
                            } // if the directory existed
                        } // if the data directory exists
                    } // data directory (expresionDistributionByChromosomeMap)

                    fileIdToCaseId = new Dictionary<string, string>();

                    foreach (var caseEntry in cases)
                    {
                        var case_ = caseEntry.Value;

                        fileIdToCaseId.Add(case_.tumor_dna_file_id, case_.case_id);
                        fileIdToCaseId.Add(case_.tumor_rna_file_id, case_.case_id);
                        fileIdToCaseId.Add(case_.normal_dna_file_id, case_.case_id);
                        if (null != case_.normal_rna_file_id && "" != case_.normal_rna_file_id)
                        {
                            fileIdToCaseId.Add(case_.normal_rna_file_id, case_.case_id);
                        }
                        if (null != case_.tumor_methylation_file_id && "" != case_.tumor_methylation_file_id)
                        {
                            fileIdToCaseId.Add(case_.tumor_methylation_file_id, case_.case_id);
                        }
						if (null != case_.normal_methylation_file_id && "" != case_.normal_methylation_file_id)
						{
							fileIdToCaseId.Add(case_.normal_methylation_file_id, case_.case_id);
						}
					}

                    //
                    // Check that the derived file cases are real cases.
                    //

                    foreach (var derivedFileCaseEntry in derivedFiles)
                    {
                        var caseId = derivedFileCaseEntry.Key;
                        var derivedFilesForThisCase = derivedFileCaseEntry.Value;

                        if (cases.ContainsKey(caseId))
                        {
                            continue;
                        }

                        Console.Write("There are derived files directories for case id " + caseId + ", which isn't a known case.  They are:");
                        var knownDirectories = new List<string>();
                        foreach (var badDrivedFile in derivedFilesForThisCase)
                        {
                            var containingDirectory = ASETools.GetDirectoryFromPathname(badDrivedFile.fileinfo.FullName);
                            if (knownDirectories.Contains(containingDirectory))
                            {
                                continue;
                            }

                            Console.Write(" " + containingDirectory);

                            knownDirectories.Add(containingDirectory);
                        }
                        Console.WriteLine();
                    }


                    ASETools.Case.loadAllFileLocations(configuration, cases, downloadedFiles, derivedFiles);

                    int nNormalDNA = 0, nTumorDNA = 0, nNormalRNA = 0, nTumorRNA = 0, nMethylation = 0, nCopyNumber = 0;
                    ulong bytesNormalDNA = 0, bytesTumorDNA = 0, bytesNormalRNA = 0, bytesTumorRNA = 0, bytesMethylation = 0, bytesCopyNumber = 0;


                    foreach (var caseEntry in cases)
                    {
                        var case_ = caseEntry.Value;

                        if (downloadedFiles.ContainsKey(case_.normal_dna_file_id))
                        {
                            nNormalDNA++;
                            bytesNormalDNA += (ulong)downloadedFiles[case_.normal_dna_file_id].fileInfo.Length;
                        }

                        if (downloadedFiles.ContainsKey(case_.tumor_dna_file_id))
                        {
                            nTumorDNA++;
                            bytesTumorDNA += (ulong)downloadedFiles[case_.tumor_dna_file_id].fileInfo.Length;
                        }

                        if (downloadedFiles.ContainsKey(case_.normal_rna_file_id))
                        {
                            nNormalRNA++;
                            bytesNormalRNA += (ulong)downloadedFiles[case_.normal_rna_file_id].fileInfo.Length;
                        }

                        if (downloadedFiles.ContainsKey(case_.tumor_rna_file_id))
                        {
                            nTumorRNA++;
                            bytesTumorRNA += (ulong)downloadedFiles[case_.tumor_rna_file_id].fileInfo.Length;
                        }

                        if (downloadedFiles.ContainsKey(case_.normal_methylation_file_id))
                        {
                            nMethylation++;
                            bytesMethylation += (ulong)downloadedFiles[case_.normal_methylation_file_id].fileInfo.Length;
                        }

						if (downloadedFiles.ContainsKey(case_.tumor_methylation_file_id))
						{
							nMethylation++;
							bytesMethylation += (ulong)downloadedFiles[case_.tumor_methylation_file_id].fileInfo.Length;
						}

						if (downloadedFiles.ContainsKey(case_.normal_copy_number_file_id))
                        {
                            nCopyNumber++;
                            bytesCopyNumber += (ulong)downloadedFiles[case_.normal_copy_number_file_id].fileInfo.Length;
                        }

						if (downloadedFiles.ContainsKey(case_.tumor_copy_number_file_id))
						{
							nCopyNumber++;
							bytesCopyNumber += (ulong)downloadedFiles[case_.tumor_copy_number_file_id].fileInfo.Length;
						}

                        fileSizesFromGDC.Add(case_.normal_dna_file_id, case_.normal_dna_size);
                        fileSizesFromGDC.Add(case_.tumor_dna_file_id, case_.tumor_dna_size);
                        fileSizesFromGDC.Add(case_.tumor_rna_file_id, case_.tumor_rna_size);

                        if (case_.normal_rna_file_id != "")
                        {
                            fileSizesFromGDC.Add(case_.normal_rna_file_id, case_.normal_rna_size);
                        }

                        if (case_.tumor_methylation_file_id != "")
                        {
                            fileSizesFromGDC.Add(case_.tumor_methylation_file_id, case_.tumor_methylation_size);
                        }

                        if (case_.normal_methylation_file_id != "")
                        {
                            fileSizesFromGDC.Add(case_.normal_methylation_file_id, case_.normal_methylation_size);
                        }

                        if (case_.tumor_copy_number_file_id != "")
                        {
                            fileSizesFromGDC.Add(case_.tumor_copy_number_file_id, case_.tumor_copy_number_size);
                        }

                        if (case_.normal_copy_number_file_id != "")
                        {
                            fileSizesFromGDC.Add(case_.normal_copy_number_file_id, case_.normal_copy_number_size);
                        }

                        if (case_.tumor_fpkm_file_id != "")
                        {
                            fileSizesFromGDC.Add(case_.tumor_fpkm_file_id, case_.tumor_fpkm_size);
                        }

                        if (case_.normal_fpkm_file_id != "")
                        {
                            fileSizesFromGDC.Add(case_.normal_fpkm_file_id, case_.normal_fpkm_size);
                        }
                    } // foreach case


                    Console.WriteLine(nNormalDNA + "(" + ASETools.SizeToUnits(bytesNormalDNA) + "B) normal DNA, " + nTumorDNA + "(" + ASETools.SizeToUnits(bytesTumorDNA) + "B) tumor DNA, " +
                                      nNormalRNA + "(" + ASETools.SizeToUnits(bytesNormalRNA) + "B) normal RNA, " + nTumorRNA + "(" + ASETools.SizeToUnits(bytesTumorRNA) + "B) tumor RNA, " +
                                      nMethylation + "(" + ASETools.SizeToUnits(bytesMethylation) + "B) methylation, " + nCopyNumber + "(" + ASETools.SizeToUnits(bytesCopyNumber) + "B) copy number");

                } // If we loaded cases

                expressionFiles = new Dictionary<string, FileInfo>();

                if (Directory.Exists(configuration.expressionFilesDirectory) && null != cases)
                {
                    foreach (var filename in Directory.EnumerateFiles(configuration.expressionFilesDirectory, "expression_*")) {
                        var disease = filename.Substring(filename.LastIndexOf('_') + 1).ToLower();
                        if (!diseases.Contains(disease))
                        {
                            Console.WriteLine("Found expression file that doesn't seem to correspond to a disease: " + filename);
                        }
                        else
                        {
                            expressionFiles.Add(disease, new FileInfo(filename));
                        }
                    }
                }
            } // DetermineTheStateOfTheWorld

            public bool fileDownloadedAndVerified(string file_id, string expectedMD5)
            {
                return downloadedFiles.ContainsKey(file_id) && (null == expectedMD5 || "" == expectedMD5 || downloadedFiles[file_id].storedMD5 == expectedMD5);
            }

            public bool containsDerivedFile(string case_id, string derived_from_file_id, ASETools.DerivedFile.Type type)
            {
                return derivedFiles.ContainsKey(case_id) && derivedFiles[case_id].Where(x => x.derived_from_file_id == derived_from_file_id && x.type == type).Count() != 0;
            }

            public ASETools.DerivedFile getDrivedFile(string case_id, string derived_from_file_id, ASETools.DerivedFile.Type type)
            {
                if (!derivedFiles.ContainsKey(case_id))
                {
                    return null;
                }

                var set = derivedFiles[case_id].Where(x => x.derived_from_file_id == derived_from_file_id && x.type == type);

                if (set.Count() == 0)
                {
                    return null;
                }

                return set.ToList()[0];
            }
        } // StateOfTheWorld

        class SingleReadPhasingProcessingStage : SingleOutputProcessingStage
        {
            public SingleReadPhasingProcessingStage() : base("Single Read-Based Phasing", true, "SingleReadPairPhasing.exe", "", getCaseFiles, getOneOffInputs, getOutputFile)
            { }

            static GetOneOffFile[] getOneOffInputs = { _ => _.configuration.finalResultsDirectory + ASETools.ASECorrectionFilename };
            static GetCaseFile[] getCaseFiles = { _ => _.tumor_dna_reads_at_tentative_selected_variants_filename, _ => _.tumor_rna_reads_at_tentative_selected_variants_filename, _ => _.extracted_maf_lines_filename };
            static GetOneOffFile[] getOutputFile = { _ => _.configuration.finalResultsDirectory + ASETools.SingleReadPhasingFilename };
        }

        class TentativeASVWithoutCNVsProcessingStage : PerCaseProcessingStage
        {
            public TentativeASVWithoutCNVsProcessingStage() : base("Tentative ASV without CNVs", "GenerateTentativeASVsWithoutCNVs.exe", "", getCaseFiles, getOneOffInputs, getOutputFile)
            { }

            static GetOneOffFile[] getOneOffInputs = { _ => _.configuration.finalResultsDirectory + ASETools.ASECorrectionFilename };
            static GetCaseFile[] getCaseFiles = { _ => _.tentative_annotated_selected_variants_filename, _ => _.tumor_copy_number_filename };
            static GetCaseFile[] getOutputFile = { _ => _.tentative_asv_without_cnvs_filename };
        }

        class CompressVCFProcessingStage : PerCaseProcessingStage
        {
            public CompressVCFProcessingStage() : base("Compress VCFs", "CompressVCF.exe", "", getInputFile, null, getOutputFile) { }

            static GetCaseFile[] getInputFile = { _ => _.vcf_filename };
            static GetCaseFile[] getOutputFile = { _ => _.compressed_vcf_filename };
        }

        class VariantPhasingProcessingStage : PerCaseProcessingStage
        {
            public VariantPhasingProcessingStage() : base("Phase variants", "CheckPhasing.exe", "", getInputFiles, getOneOffInputs, getOutputFile) { }

            static GetOneOffFile[] getOneOffInputs = { _ => _.configuration.finalResultsDirectory + ASETools.ASECorrectionFilename };
            static GetCaseFile[] getInputFiles = {_ => _.tentative_annotated_selected_variants_filename, _ => _.tumor_dna_reads_at_tentative_selected_variants_filename, _ => _.tumor_dna_reads_at_tentative_selected_variants_index_filename,
                                                  _ => _.tumor_rna_reads_at_tentative_selected_variants_filename, _ => _.tumor_rna_reads_at_tentative_selected_variants_index_filename};

            static GetCaseFile[] getOutputFile = { _ => _.variant_phasing_filename };
        }

        class ExtractVCFStatisticsProcessingStage : PerCaseProcessingStage
        {
            public ExtractVCFStatisticsProcessingStage() : base("Extract VCF Statistics", "ExtractVCFStatistics.exe", "", getInputFile, null, getOutputFile) { }

            static GetCaseFile[] getInputFile = { _ => _.vcf_filename };
            static GetCaseFile[] getOutputFile = { _ => _.vcf_statistics_filename };
        } // ExtractVCFStatisticsProcessingStage

        class OverallVCFStatisticsProcessingStage : SingleOutputProcessingStage
        {
            public OverallVCFStatisticsProcessingStage() : base("Overall VCF Statistics", true, "VCFStatistics.exe", "", getInputFiles, null, getOutputFile) { }

            static GetCaseFile[] getInputFiles = { _ => _.variant_phasing_filename, _=> _.annotated_selected_variants_filename };
            static GetOneOffFile[] getOutputFile = { _ => _.configuration.finalResultsDirectory + ASETools.VCFStatisticsFilename };
        } // OverallVCFStatisticsProcessingStage

        class miRNAProcessingStage : SingleOutputProcessingStage
        {
            public miRNAProcessingStage() : base("miRNA Expression Analysis", true, "AnalyzeMiRNAExpression.exe", "", getCaseFiles, null, getOutputFiles, true) { }
            static GetCaseFile[] getCaseFiles = { _ => _.tumor_miRNA_expression_quantification_filename };
            static GetOneOffFile[] getOutputFiles = { _ => _.configuration.finalResultsDirectory + ASETools.miRNAExpressionSummaryFilename, _ => _.configuration.finalResultsDirectory + ASETools.miRNAExpressionPValueHistogramFilename };
        }

        class SNAPRealignmentProcessingStage : ProcessingStage
        {
            public SNAPRealignmentProcessingStage(bool tumor_)
            {
                tumor = tumor_;
            }

            public string GetStageName()
            {
                return "SNAP " + (tumor ? "tumor" : "normal") + " realignment";
            }

            public bool NeedsCases() { return true; }

            public bool EvaluateDependencies(StateOfTheWorld stateOfTheWorld)
            {
                return true; // Just depends on the downloaded BAM file
            }

            public void EvaluateStage(StateOfTheWorld stateOfTheWorld, StreamWriter script, ASETools.RandomizingStreamWriter hpcScript, StreamWriter linuxScript,
                StreamWriter azureScript, out List<string> filesToDownload, out int nDone, out int nAddedToScript, out int nWaitingForPrerequisites)
            {
                nDone = nAddedToScript = nWaitingForPrerequisites = 0;
                filesToDownload = null;

                if (stateOfTheWorld.caseMetadata == null)
                {
                    nWaitingForPrerequisites = 1;
                    return;
                }

                ASETools.Case.ColumnGetter getInputFilename = c => (tumor ? c.tumor_dna_filename : c.normal_dna_filename);
                ASETools.Case.ColumnGetter getFinishedFilename = c => (tumor ? c.snap_realigned_tumor_dna_filename : c.snap_realigned_normal_dna_filename);
                ASETools.Case.ColumnGetter getBaiFilename = c => (tumor ? c.snap_realigned_tumor_dna_bai_filename : c.snap_realigned_normal_dna_bai_filename);
                ASETools.Case.ColumnGetter getRealignmentStatsFilename = c => (tumor ? c.snap_realigned_tumor_dna_statictics_filename : c.snap_realigned_normal_dna_statictics_filename);

                foreach (var case_ in stateOfTheWorld.listOfCases)
                {
                    var fileId = tumor ? case_.tumor_dna_file_id : case_.normal_dna_file_id;

                    if (getInputFilename(case_) == "" || case_.case_metadata_filename == "")
                    {
                        nWaitingForPrerequisites++;
                    } 
                    else if (getFinishedFilename(case_) != "" && getRealignmentStatsFilename(case_) != "" && getBaiFilename(case_) != "")
                    {
                        nDone++;
                    } 
                    else
                    {
                        var bamMetadata = stateOfTheWorld.caseMetadata[case_.case_id].getBAMMetadata(tumor, true);

                        nAddedToScript++;
                        script.WriteLine(stateOfTheWorld.configuration.binariesDirectory + "SnapTimer.exe " +
                            ASETools.GetDirectoryFromPathname(case_.case_metadata_filename) + @"\" + fileId + (tumor ? ASETools.snapRealignedTumorDNAStatisticsExtension : ASETools.snapRealignedNormalDNAStaticticsExtension) + " " +
                            (bamMetadata.isPaired ? "paired " : "single ") + stateOfTheWorld.configuration.localIndexDirectory + " -map -so -sm 60 " + getInputFilename(case_) +
                            @" -mrl 40 -sid d:\temp\ -o " + ASETools.GetDerivedFiledDirectoryFromFilename(getInputFilename(case_), stateOfTheWorld.configuration) + case_.case_id + @"\" +
                            fileId + (tumor ? ASETools.snapRealignedTumorDNAExtension : ASETools.snapRealignedNormalDNAExtension));
                    } // We thought we had everything
                }// for each case 
            } // EvaluateStage


            bool tumor;
        } // SNAPRealignmentProcessingStage

        class FASTQGenerationProcessingStage : ProcessingStage
        {
            public string GetStageName() { return "FASTQ Generation"; }
            public bool NeedsCases() { return true; }
            public bool EvaluateDependencies(StateOfTheWorld stateOfTheWorld)
            {
                return true; // Just depends on the downloaded BAM file
            }

            public void EvaluateStage(StateOfTheWorld stateOfTheWorld, StreamWriter script, ASETools.RandomizingStreamWriter hpcScript, StreamWriter linuxScript,
                                        StreamWriter azureScript, out List<string> filesToDownload, out int nDone, out int nAddedToScript, out int nWaitingForPrerequisites)
            {
                filesToDownload = null;
                nDone = nAddedToScript = nWaitingForPrerequisites = 0;

                if (stateOfTheWorld.caseMetadata == null)
                {
                    nWaitingForPrerequisites = 1;
                    return;
                }

                foreach (var case_ in stateOfTheWorld.commonData.listOfCases)
                {
                    var paired = stateOfTheWorld.caseMetadata[case_.case_id].getBAMMetadata(false, true).isPaired;

                    if (case_.normal_dna_fastq_filename != "" && (!paired || case_.normal_dna_fastq_second_end != ""))
                    {
                        nDone++;
                    } else if (case_.normal_dna_filename == "")
                    {
                        nWaitingForPrerequisites++; // Really a download, but we'll just call it a prereq.
                    } else
                    {
                        var filteredBAMFilename = "/mnt/d/temp/" + case_.normal_dna_file_id + ".filtered_supplementary.bam ";
                        var sortedFilteredBAMFilename = "/mnt/d/temp/" + case_.normal_dna_file_id + ".filtered_supplementary_name_sorted.bam";
                        var fastq1Filename = "/mnt/d/temp" + case_.normal_dna_file_id + ASETools.normalFastqExtension;
                        var fastq2Filename = "/mnt/d/temp" + case_.normal_dna_file_id + ASETools.normalSecondEndFastqExtension;
                        var copiedBamDirectory = "~/" + case_.normal_dna_file_id;
                        var copiedInputFile = copiedBamDirectory + ASETools.GetFileNameFromPathname(case_.normal_dna_filename);

                        linuxScript.Write("date\n");

                        var sourceMountpoint = "/mnt/" + ASETools.ComputerFromPathname(case_.normal_dna_filename);
                        linuxScript.Write("sudo mkdir " + sourceMountpoint + "\n");
                        linuxScript.Write("sudo chmod 777 " + sourceMountpoint + "\n");
                        linuxScript.Write("sudo mount -t drvfs '" + ASETools.ShareFromPathname(case_.normal_dna_filename) + "' " + sourceMountpoint + "\n");

                        linuxScript.Write("mkdir " + copiedBamDirectory + "\n");
                        linuxScript.Write("cp " + sourceMountpoint + ASETools.PathnameToLinuxPathname(ASETools.PathnameWithoutUNC(case_.normal_dna_filename)) + " " + copiedBamDirectory + "/\n");
                        linuxScript.Write("cp " + sourceMountpoint + ASETools.PathnameToLinuxPathname(ASETools.PathnameWithoutUNC(ASETools.GetDirectoryFromPathname(case_.normal_dna_filename))) + "/*bai " + copiedBamDirectory + "/\n");
                        linuxScript.Write("sudo umount " + sourceMountpoint);

                        linuxScript.Write("samtools view -F 2048 -b -o " + filteredBAMFilename + " " + copiedInputFile + "\n");
                        linuxScript.Write("rm -rf " + copiedBamDirectory + "\n");
                        linuxScript.Write("samtools sort -n -m 50G -@ 16  -o " + sortedFilteredBAMFilename + " " + filteredBAMFilename + "\n");
                        if (paired)
                        {
                            linuxScript.Write("bedtools bamToFastq -i " + sortedFilteredBAMFilename + " -fq " + fastq1Filename + " -fq2 " + fastq2Filename + "\n");
                        }
                        else 
                        {
                            linuxScript.Write("bedtools bamToFastq -i " + sortedFilteredBAMFilename + " -fq " + fastq1Filename + "\n");
                        }

                        var destinationDownloadDirectory = stateOfTheWorld.randomFilesystemByFreeSpace.select();
                        var destinationMountpoint = "/mnt/" + ASETools.ComputerFromPathname(destinationDownloadDirectory);
                        var destinationDirectory = destinationDownloadDirectory + @"..\derived_files\" + case_.case_id + @"\";
                        linuxScript.Write("sudo mkdir " + destinationMountpoint + "\n");
                        linuxScript.Write("sudo chmod 777 " + destinationMountpoint + "\n");
                        linuxScript.Write("sudo mount -t drvfs '" + ASETools.ShareFromPathname(destinationDownloadDirectory) + "' " + destinationMountpoint + "\n");
                        linuxScript.Write("cp " + fastq1Filename + " " + destinationMountpoint + ASETools.WindowsToLinuxPathname(ASETools.PathnameWithoutUNC(destinationDirectory)));
                        if (paired)
                        {
                            linuxScript.Write("cp " + fastq2Filename + " " + destinationMountpoint + ASETools.WindowsToLinuxPathname(ASETools.PathnameWithoutUNC(destinationDirectory)));
                            linuxScript.Write("rm " + fastq2Filename);
                        }
                        linuxScript.Write("rm " + filteredBAMFilename + " " + sortedFilteredBAMFilename + " " + fastq1Filename +"\n");
                        linuxScript.Write("sudo umount " + destinationMountpoint);

                        nAddedToScript++;
                    } // if we're doing it
                } // foreach case 
            } // EvaluateStage

        } // FASTQGenerationProcessingStage


        static void Main(string[] args)
        {
            var stopwatch = new Stopwatch();
            stopwatch.Start();

            var configuration = ASETools.Configuration.loadFromFile(args);

            if (null == configuration)
            {
                Console.WriteLine("Giving up because we were unable to load configuration.");
                return;
            }

            if (configuration.commandLineArgs.Count() > 1 || configuration.commandLineArgs.Count() == 1 && configuration.commandLineArgs[0] != "-d")
            {
                Console.WriteLine("usage: ASEProcessManager {-configuration configurationFilename} {-d}");
                Console.WriteLine("-d means to check dependencies.");
            }
            
            //
            // Delete any existing scripts.
            //
            File.Delete(configuration.scriptOutputDirectory + scriptFilename);
            File.Delete(configuration.scriptOutputDirectory + linuxScriptFilename);
            File.Delete(configuration.scriptOutputDirectory + downloadScriptFilename);
            if (configuration.hpcScriptFilename != "")
            {
                File.Delete(configuration.scriptOutputDirectory + configuration.hpcScriptFilename);
            }
            if (configuration.azureScriptFilename != "")
            {
                File.Delete(configuration.scriptOutputDirectory + configuration.azureScriptFilename);
            }

            bool checkDependencies = configuration.commandLineArgs.Count() >= 1 && configuration.commandLineArgs.Contains("-d");

            var stateOfTheWorld = new StateOfTheWorld(configuration);
            stateOfTheWorld.DetermineTheStateOfTheWorld(args);

            foreach (var directoryToEnsure in stateOfTheWorld.configuration.neededGlobalDirectories())
            {
                Directory.CreateDirectory(directoryToEnsure);
            }

            jobAddString = @"job add %1 /exclusive /numnodes:1-1 /scheduler:" + stateOfTheWorld.configuration.hpcScheduler + " ";
            
            Console.WriteLine();

            if (null != stateOfTheWorld.cases)
            {
                //
                // Rewrite the cases file, since we have just updated all of the file locations for downloaded and derived files.
                //
                ASETools.Case.SaveToFile(stateOfTheWorld.cases, configuration.casesFilePathname);
            }

            var script = ASETools.CreateStreamWriterWithRetry(configuration.scriptOutputDirectory + scriptFilename);

            if (configuration.completedVCFsDirectory != "" && stateOfTheWorld.cases != null)
            {
                //
                // Check to see if there are any completed VCFs (downloaded from Auzure) that need to be moved.
                //

                var casesByNormalDNAId = new Dictionary<string, ASETools.Case>();
                foreach (var caseEntry in stateOfTheWorld.cases) {
                    var case_ = caseEntry.Value;
                    casesByNormalDNAId.Add(case_.normal_dna_file_id, case_);
                }


                var vcfsToBeMoved = new List<string>();
                foreach (var completedVCF in Directory.EnumerateFiles(configuration.completedVCFsDirectory)) {
                    if (completedVCF.ToLower().EndsWith("filepart"))
                    {
                        //
                        // This is a partially downloaded file from WinSCP.  Just ignore it.
                        //
                        continue;
                    }
                    
                    if (!completedVCF.EndsWith(ASETools.vcfExtension))
                    {
                        Console.WriteLine("Found non-VCF file in completed VCFs directory: " + completedVCF + ".  Ignoring.");
                        continue;
                    }

                    string fileId = ASETools.GetFileIdFromPathname(completedVCF);
                    if (!casesByNormalDNAId.ContainsKey(fileId)) {
                        Console.WriteLine("completed VCFs directory contains a file that doesn't seem to correspond to a normal DNA file id: " + completedVCF + ".  Ignoring.");
                        continue;
                    }

                    vcfsToBeMoved.Add(completedVCF);
                }

                if (vcfsToBeMoved.Count() > 0) {
                    var completedVCFsPathComponents = configuration.completedVCFsDirectory.Split('\\');

                    if (completedVCFsPathComponents.Count() < 2)
                    {
                        Console.WriteLine("The completed VCF directory in the configuration should be a pathname: " + configuration.completedVCFsDirectory);
                        return;
                    }


                    bool failed = false;
                    string [] dataPathComponents = null;
                    int completedComponentsToSkip = configuration.completedVCFsDirectory.EndsWith(@"\") ? 2 : 1;

                    foreach (var dataDirectory in configuration.dataDirectories)
                    {
                        dataPathComponents = dataDirectory.Split('\\');

                        failed = false;
                        for (int i = 0; i < completedVCFsPathComponents.Count() - completedComponentsToSkip; i++)
                        {
                            if (dataPathComponents[i] != completedVCFsPathComponents[i])
                            {
                                failed = true;
                                break;
                            }
                        }

                        if (!failed) {
                            break;
                        }
                    } // foreach data directory

                    if (failed) {
                        Console.WriteLine("Unable to find destination for completed VCFs (the completed VCFs directory doesn't share a parent with any data directory, and it must.)  Look at your configuration file.");
                        return;
                    }


                    string destinationDirectory = dataPathComponents[0];

                    for (int i = 1; i < completedVCFsPathComponents.Count() - completedComponentsToSkip; i++)
                    {
                        destinationDirectory += '\\' + dataPathComponents[i];
                    }

                    destinationDirectory += '\\' + configuration.derivedFilesDirectory + '\\';

                    foreach (var completedVCF in vcfsToBeMoved)
                    {
                        string normalDNAFileId = ASETools.GetFileIdFromPathname(completedVCF);

                        var case_ = casesByNormalDNAId[normalDNAFileId];
                        script.WriteLine("md " + destinationDirectory + case_.case_id + " & " + "mv " + completedVCF + " " + destinationDirectory + case_.case_id + @"\" + ASETools.GetFileNameFromPathname(completedVCF));
                    }

                    Console.WriteLine("Added " + vcfsToBeMoved.Count() + " vcfs to be moved from the completed_vcfs directory to their final locations.");
                }// If we had any completed VCFs to be moved.
            } // if we have a completed VCFs directory


            List<ProcessingStage> processingStages = new List<ProcessingStage>();

            processingStages.Add(new MAFConfigurationProcessingStage());
			processingStages.Add(new GenerateCasesProcessingStage());
			processingStages.Add(new AllcountProcesingStage());
			processingStages.Add(new DownloadProcessingStage());
			processingStages.Add(new MD5ComputationProcessingStage());
			processingStages.Add(new GermlineVariantCallingProcessingStage());
			processingStages.Add(new SelectVariantsProcessingStage());
			processingStages.Add(new AnnotateVariantsProcessingStage(stateOfTheWorld));
			processingStages.Add(new ExpressionDistributionProcessingStage(stateOfTheWorld));
			processingStages.Add(new ExtractMAFLinesProcessingStage());
			processingStages.Add(new RegionalExpressionProcessingStage());
            processingStages.Add(new ExpressionNearMutationsProcessingStage());
            processingStages.Add(new AlleleSpecificExpressionNearMutationsProcessingStage());
#if true
            processingStages.Add(new ExtractReadsProcessingStage(stateOfTheWorld, true, true));
            processingStages.Add(new ExtractReadsProcessingStage(stateOfTheWorld, true, false));
            processingStages.Add(new ExtractReadsProcessingStage(stateOfTheWorld, false, true));
            processingStages.Add(new ExtractReadsProcessingStage(stateOfTheWorld, false, false));
#else
            processingStages.Add(new ExtractReadsProcessingStage());
#endif
            processingStages.Add(new AddPercentilesToScatterGraphLinesProcessingStage());
            processingStages.Add(new SelectGenesProcessingStage());
			processingStages.Add(new CountMappedBasesProcessingStage());
			processingStages.Add(new GenerateScatterGraphsProcessingStage());
			//processingStages.Add(new MethylationProcessingStage());
			processingStages.Add(new FPKMProcessingStage());
            processingStages.Add(new GenerateAllLociProcessingStage());
            processingStages.Add(new AlignAllLociProcessingStage());
            processingStages.Add(new RepetitveRegionMapProcessingStage());
            processingStages.Add(new OverallDistributionProcessingStage());
            processingStages.Add(new CorrectionProcessingStage());
            processingStages.Add(new AllSitesReadDepthProcessingStage());
            processingStages.Add(new ExpressionByMutationCountProcessingStage());
            processingStages.Add(new BonferroniProcessingStage());
            processingStages.Add(new ASEConsistencyProcessingStage());
            processingStages.Add(new ASEMapProcessingStage());
            processingStages.Add(new ZeroOneTwoProcessingStage());
            processingStages.Add(new MannWhitneyProcessingStage());
            processingStages.Add(new PerCaseASEProcessingStage());
            processingStages.Add(new CategorizeTumorsProcessingStage());
            processingStages.Add(new SelectRegulatoryMAFLinesProcessingStage());
            processingStages.Add(new MappedBaseCountDistributionProcessingStage());
            processingStages.Add(new AnnotateRegulatoryRegionsProcessingStage());
            processingStages.Add(new RegulatoryMutationsNearMutationsProcessingStage());
            processingStages.Add(new RegulatoryMutationsByVAFProcessingStage());
            processingStages.Add(new IndexBAMsProcessingStage());
            processingStages.Add(new SortBAMsProcessingStage());
            processingStages.Add(new VAFHistogramsProcessingStage());
            processingStages.Add(new ASEScatterProcessingStage());
            processingStages.Add(new ExpressionByGeneProcessingStage());
            processingStages.Add(new BasesInKnownCodingRegionsProcessingStage());
            processingStages.Add(new OverallGeneExpressionProcessingStage());
            processingStages.Add(new AnnotateScatterGraphsProcessingStage());
            processingStages.Add(new GenerateTranscriptomeReadsAndReferenceProcessingStage());
            processingStages.Add(new GenerateTranscriptomeIndexProcessingStage());
            processingStages.Add(new SpliceosomeAllelicImbalanceProcessingStage());
            processingStages.Add(new ReadLengthDistributionProcessingStage());
            processingStages.Add(new ChooseAnnotatedVariantsProcessingStage());
            processingStages.Add(new ExtractIsoformReadCountsProcessingStage(stateOfTheWorld));
            processingStages.Add(new ExpressionDecilesByChromosomeProcessingStage());
            processingStages.Add(new DistanceBetweenMutationsProcessingStage());
            processingStages.Add(new SingleReadPhasingProcessingStage());
            processingStages.Add(new CompressVCFProcessingStage());
            processingStages.Add(new CaseMetadataProcessingStage());
            processingStages.Add(new UniparentalDisomyProcessingStage());
            processingStages.Add(new TentativeASVWithoutCNVsProcessingStage());
            processingStages.Add(new VariantPhasingProcessingStage());
            processingStages.Add(new SummarizePhasingProcessingStage());
            processingStages.Add(new ExtractVCFStatisticsProcessingStage());
            processingStages.Add(new OverallVCFStatisticsProcessingStage());
            processingStages.Add(new ReadStaticticsProcessingStage());
            processingStages.Add(new miRNAProcessingStage());
            processingStages.Add(new SNAPRealignmentProcessingStage(false)); // Only do normal for now (tumor == false)
            processingStages.Add(new SummarizeCaseMetadataProcessingStage());
            processingStages.Add(new ConsolodatedCaseMetadataProcessingStage());
            processingStages.Add(new FASTQGenerationProcessingStage());

            if (checkDependencies)
            {
                bool allDependenciesOK = true;
                foreach (var processingStage in processingStages)
                {
                    if (stateOfTheWorld.cases != null || !processingStage.NeedsCases())
                    {
                        allDependenciesOK &= processingStage.EvaluateDependencies(stateOfTheWorld);
                    }
                }

                if (!allDependenciesOK)
                {
                    Console.WriteLine("Not generating scripts because some dependencies have been violated.  Delete the stale generated files and rerun.");
                    return;
                }
            }

            ASETools.RandomizingStreamWriter hpcScript;
            StreamWriter azureScript;

            if (configuration.hpcScriptFilename == "")  // The empty string means not to generate an output.  We do this by making a Null stream.
            {
                hpcScript = new ASETools.RandomizingStreamWriter(new StreamWriter(Stream.Null));
            }
            else
            {
                hpcScript = new ASETools.RandomizingStreamWriter(ASETools.CreateStreamWriterWithRetry(configuration.scriptOutputDirectory + configuration.hpcScriptFilename));
            }


            if (configuration.azureScriptFilename == "")
            {
                azureScript = new StreamWriter(Stream.Null);
            }
            else
            {
                azureScript = ASETools.CreateStreamWriterWithRetry(configuration.scriptOutputDirectory + configuration.azureScriptFilename);
            }

            var linuxScript = ASETools.CreateStreamWriterWithRetry(configuration.scriptOutputDirectory + linuxScriptFilename);

            var allFilesToDownload = new List<string>();

            int longestStageName = 0;

            foreach (var processingStage in processingStages)
            {
                longestStageName = Math.Max(longestStageName, processingStage.GetStageName().Count());
            }

            const string stageNameHeader = "Stage Name";

            Console.Write(stageNameHeader);
            int paddingSize = Math.Max(0, longestStageName - stageNameHeader.Count());
            for (int i = 0; i < paddingSize; i++)
            {
                Console.Write(" ");
            }

            Console.WriteLine(" # Done  # Added  # Waiting  # Downloads");

            for (int i = 0; i < stageNameHeader.Count() + paddingSize; i++) {
                Console.Write("-");
            }
            Console.WriteLine(" ------  -------  ---------  -----------");

            int totalDone = 0, totalAdded = 0, totalWaiting = 0, totalDownloads = 0;

            foreach (var processingStage in processingStages)
            {
                int nDone;
                int nAddedToScript;
                int nWaitingForPrerequisites;
                List<string> stageFilesToDownload;

                if (stateOfTheWorld.cases != null || !processingStage.NeedsCases())
                {
                    processingStage.EvaluateStage(stateOfTheWorld, script, hpcScript, linuxScript, azureScript, out stageFilesToDownload, out nDone, out nAddedToScript, out nWaitingForPrerequisites);
                }
                else
                {
                    nDone = 0;
                    nAddedToScript = 0;
                    nWaitingForPrerequisites = 1;
                    stageFilesToDownload = null;
                }

                int nDownloadsRequested = 0;
                if (null != stageFilesToDownload)
                {
                    foreach (var file in stageFilesToDownload)
                    {
                        if (!allFilesToDownload.Contains(file))
                        {
                            nDownloadsRequested++;
                            allFilesToDownload.Add(file);
                        }
                    }
                }

                Console.WriteLine(String.Format("{0," + (stageNameHeader.Count() + paddingSize) + "}", processingStage.GetStageName()) + " " + String.Format("{0,6}", nDone) + " " + String.Format("{0,8}", nAddedToScript) + " " +
                    String.Format("{0,10}", nWaitingForPrerequisites) + " " + String.Format("{0,11}", nDownloadsRequested));

                totalDone += nDone;
                totalAdded += nAddedToScript;
                totalWaiting += nWaitingForPrerequisites;
                totalDownloads += nDownloadsRequested;
            } // foreach stage

            for (int i = 0; i < stageNameHeader.Count() + paddingSize; i++)
            {
                Console.Write(" ");
            }
            Console.WriteLine(" ------  -------  ---------  -----------");
            for (int i = 0; i < stageNameHeader.Count() + paddingSize; i++)
            {
                Console.Write(" ");
            }
            Console.WriteLine(" " + String.Format("{0,6}", totalDone) + " " + String.Format("{0,8}", totalAdded) + " " +
                String.Format("{0,10}", totalWaiting) + " " + String.Format("{0,11}", totalDownloads));

            //
            // Now put downloads in their own script. They're separated out because they need to be run in one of the download
            // directories, and the user may want to split them across machines.
            //

            long bytesToDownload = 0;
            if (allFilesToDownload.Count() == 0)
            {
                File.Delete(configuration.scriptOutputDirectory + downloadScriptFilename);
            } 
            else
            {
                var downloadScript = ASETools.CreateStreamWriterWithRetry(configuration.scriptOutputDirectory + downloadScriptFilename);

                foreach (var file in allFilesToDownload)
                {
                    if (stateOfTheWorld.configuration.isBeatAML)
                    {
                        //
                        // We don't really "download" these files, they're already here from synapse.  Instead, we copy them out to new directories so we've got the directory structure we want, and so we're not
                        // overwriting the synapse directory.
                        //
                        // The fileID is of the form seqId-{r|d}na.  dna files are in synapseDir\seqcap\bam\, and rna files are in synapseDir\rnaseq\bam\
                        //
                        downloadScript.WriteLine("md " + file);
                        var unpaddedFilename = ASETools.ExtractUnderlyingStringFromGuidPaddedString(file);
                        var seqId = file.Substring(0, unpaddedFilename.Count() - 4);
                        var rna = unpaddedFilename[unpaddedFilename.Count() - 3] == 'r';
                        downloadScript.WriteLine("copy " + stateOfTheWorld.configuration.synapseDirectory + (rna ? "rnaseq" : "seqcap") + @"\bam\*" + seqId + "*.ba* " + file + @"\");
                    }
                    else
                    {
                        downloadScript.WriteLine(configuration.binariesDirectory + "gdc-client download --no-file-md5sum --token-file " + configuration.accessTokenPathname + " " + file);    // use the no MD5 sum option because we compute it ourselves later (and all a failed check does is print a message that we'll never see anyway)
                    }

                    if (stateOfTheWorld.fileSizesFromGDC.ContainsKey(file)) // MAF files aren't in here.
                    {
                        bytesToDownload += stateOfTheWorld.fileSizesFromGDC[file];
                    }
                }

                downloadScript.Close();
            }

            script.Close();
            hpcScript.Close();
            linuxScript.Close();
            azureScript.Close();

            stateOfTheWorld.expressionDistributionByChromosomeMap.WriteToFile(configuration.expression_distribution_by_chromosome_map_filename);

            Console.WriteLine();
            Console.WriteLine("Downloading " + ASETools.SizeToUnits((ulong)bytesToDownload) + "B in " + allFilesToDownload.Count() + " files.");
            Console.WriteLine("ASEProcessManager took " + ASETools.ElapsedTimeInSeconds(stopwatch) + " and finished at " + DateTime.Now.ToLocalTime().ToString());
        }
    }
}
