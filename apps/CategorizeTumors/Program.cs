﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.IO;
using System.Diagnostics;
using ASELib;

namespace CategorizeTumors
{
    class Program
    {

        class Result : IComparable<Result>
        {
            public Result(string hugo_symbol_)
            {
                hugo_symbol = hugo_symbol_;
            }
            public readonly string hugo_symbol;
            public int nZero = 0;
            public int nMultiple = 0;
            public int nLossOfHeterozygosity = 0;
            public int nOnePlusASE = 0;
            public int nOnePlusReverseASE = 0;
            public int nMinorSubclone = 0;
            public int nNonsenseMediatedDecay = 0;
            public int nSingle = 0;
            public int nTooFewReads = 0;

            public int totalEvaluated()
            {
                return nZero + nMultiple + nLossOfHeterozygosity + nOnePlusASE + nOnePlusReverseASE + nMinorSubclone + nNonsenseMediatedDecay + nSingle;
            }

            public int totalMutant()    // Excluding minor subclones
            {
                return nMultiple + nLossOfHeterozygosity + nOnePlusASE + nOnePlusReverseASE + nSingle + nNonsenseMediatedDecay;
            }

            string frac(int numerator)
            {
                if (totalEvaluated() == 0)
                {
                    return "*";
                }

                return Convert.ToString((double)numerator / totalEvaluated());
            }

            string fracOfAllMutant(int numerator)
            {
                if (totalMutant() == 0)
                {
                    return "*";
                }

                return Convert.ToString((double)numerator / totalMutant());
            }
            public string fracZero() { return frac(nZero); }
            public string fracMultiple() { return frac(nMultiple); }
            public string fracLossOfHeterozygosity() { return frac(nLossOfHeterozygosity); }
            public string fracOnePlusASE() { return frac(nOnePlusASE); }
            public string fracOnePlusReverseASE() { return frac(nOnePlusReverseASE); }
            public string fracMinorSubclone() { return frac(nMinorSubclone); }
            public string fracNonsenseMediatedDecay() { return frac(nNonsenseMediatedDecay); }
            public string fracSingle() { return frac(nSingle); }

            public string fracOfAllMutantMultiple() { return fracOfAllMutant(nMultiple); }
            public string fracOfAllMutantLossOfHeterozygosity() { return fracOfAllMutant(nLossOfHeterozygosity); }
            public string fracOfAllMutantOnePlusASE() { return fracOfAllMutant(nOnePlusASE); }
            public string fracOfAllMutantOnePlusReverseASE() { return fracOfAllMutant(nOnePlusReverseASE); }
            public string fracOfAllMutantSingle() { return fracOfAllMutant(nSingle); }
            public string fracOfAllMutantNonsenseMediatedDecay() { return fracOfAllMutant(nNonsenseMediatedDecay); }

            public int CompareTo(Result peer)
            {
                return hugo_symbol.CompareTo(peer.hugo_symbol);
            }
        }



        static void Main(string[] args)
        {
            var stopwatch = new Stopwatch();
            stopwatch.Start();

            var tp53SingleHistogram = new ASETools.PreBucketedHistogram(0, 1, 0.01);
            var tp53MultipleHistogram = new ASETools.PreBucketedHistogram(0, 1, 0.01);
            var tp53LossOfHetHistogram = new ASETools.PreBucketedHistogram(0, 1, 0.01);

            var configuration = ASETools.Configuration.loadFromFile(args);
            if (null == configuration)
            {
                Console.WriteLine("Unable to load configuration.");
                return;
            }

            var cases = ASETools.Case.LoadCases(configuration.casesFilePathname).Select(x => x.Value).ToList();

            if (null == cases)
            {
                Console.WriteLine("Unable to load cases.  You must generate it before running this tool.");
                return;
            }

            var results = new List<Result>();

            var scatterGraphLinesByGene = ASETools.GeneScatterGraphLine.LoadAllGeneScatterGraphLines(configuration.geneScatterGraphsDirectory, true, "*").
                Where(x => x.Variant_Classification != "Silent" && x.Chromosome != "chrX" && x.Chromosome != "chrY" && x.Chromosome != "chrM" && x.Chromosome != "chrMT").
                GroupByToDict(x => x.Hugo_Symbol);    // true->from unfiltered

            Console.WriteLine("Loaded " + scatterGraphLinesByGene.Count() + " scatter graph genes with " + scatterGraphLinesByGene.Select(x => x.Value.Count()).Sum() + " lines in " + ASETools.ElapsedTimeInSeconds(stopwatch));


            Console.WriteLine("Processing " + scatterGraphLinesByGene.Count() + " genes, 1 dot/100 genes: ");
            ASETools.PrintNumberBar(scatterGraphLinesByGene.Count() / 100);
            int nGenesProcessed = 0;

            foreach (var geneEntry in scatterGraphLinesByGene)
            {
                var hugo_symbol = geneEntry.Key;
                var scatterGraphLines = geneEntry.Value;

                var result = new Result(hugo_symbol);

                foreach (var case_ in cases)
                {
                    var linesForThisCase = scatterGraphLines.Where(x => x.case_id == case_.case_id).ToList();

                    if (linesForThisCase.Count() == 0)
                    {
                        result.nZero++;
                        continue;
                    }

                    if (linesForThisCase.Count() > 1)
                    {
                        if (hugo_symbol.ToLower() == "tp53")
                        {
                            double totalVAF = 0;
                            int n = 0;
                            foreach (var oneLine in linesForThisCase)
                            {
                                if (oneLine.tumorRNAReadCounts.usefulReads() >= configuration.minRNAReadCoverage && oneLine.tumorDNAReadCounts.usefulReads() >= configuration.minDNAReadCoverage)
                                {
                                    totalVAF += oneLine.tumorRNAReadCounts.AltFraction();
                                    n++;
                                }
                            }

                            if (n > 0)
                            {
                                tp53MultipleHistogram.addValue(totalVAF / n);
                            }
                        }
                        result.nMultiple++;
                        continue;
                    }

                    var line = linesForThisCase[0];

                    if (line.tumorDNAReadCounts.usefulReads() < configuration.minDNAReadCoverage)
                    {
                        result.nTooFewReads++;
                        continue;
                    }

                    if (line.tumorDNAReadCounts.AltFraction() < 0.4)
                    {
                        result.nMinorSubclone++;
                        continue;
                    }

                    if (line.tumorDNAReadCounts.AltFraction() > 0.6)
                    {
                        if (hugo_symbol.ToLower() == "tp53" && line.tumorRNAReadCounts.usefulReads() > configuration.minRNAReadCoverage)
                        {
                            tp53LossOfHetHistogram.addValue(line.tumorRNAReadCounts.AltFraction());
                        }
                        result.nLossOfHeterozygosity++;
                        continue;
                    }

                    if (line.tumorRNAReadCounts.usefulReads() < configuration.minRNAReadCoverage)
                    {
                        result.nTooFewReads++;
                        continue;
                    }

                    if (ASETools.NonsenseMediatedDecayCausingVariantClassifications.Contains(line.Variant_Classification))
                    {
                        result.nNonsenseMediatedDecay++;
                        continue;
                    }

                    if (line.Hugo_Symbol.ToLower() == "tp53")
                    {
                        tp53SingleHistogram.addValue(line.tumorRNAReadCounts.AltFraction());
                    }

                    if (line.tumorRNAReadCounts.AltFraction() > 0.6)
                    {
                        result.nOnePlusASE++;
                        continue;
                    }

                    if (line.tumorRNAReadCounts.AltFraction() < 0.4)
                    {
                        result.nOnePlusReverseASE++;
                        continue;
                    }

                    result.nSingle++;                    
                } // foreach case

                results.Add(result);

                nGenesProcessed++;
                if (nGenesProcessed % 100 == 0)
                {
                    Console.Write(".");
                }
            } // foreach gene

            Console.WriteLine();

            var tp53File = ASETools.CreateStreamWriterWithRetry(@"\temp\tp53_histograms.txt");
            tp53File.WriteLine("Exactly one mutation (n = " + tp53SingleHistogram.count() + ")");
            tp53File.WriteLine(ASETools.HistogramResultLine.Header());
            tp53SingleHistogram.ComputeHistogram().ToList().ForEach(x => tp53File.WriteLine(x));
            tp53File.WriteLine();

            tp53File.WriteLine("Multiple mutations (n = " + tp53MultipleHistogram.count() + ")");
            tp53File.WriteLine(ASETools.HistogramResultLine.Header());
            tp53MultipleHistogram.ComputeHistogram().ToList().ForEach(x => tp53File.WriteLine(x));
            tp53File.WriteLine();

            tp53File.WriteLine("Loss of heterozygosity (n = " + tp53LossOfHetHistogram.count() + ")");
            tp53File.WriteLine(ASETools.HistogramResultLine.Header());
            tp53LossOfHetHistogram.ComputeHistogram().ToList().ForEach(x => tp53File.WriteLine(x));

            tp53File.Close();

            results.Sort();

            var outputFile = ASETools.CreateStreamWriterWithRetry(configuration.finalResultsDirectory + ASETools.Configuration.geneCategorizationFilename);

            outputFile.WriteLine("Hugo Symbol\tn Too Few Reads\tn Evaluated\tn Mutant (ignoring minor subclones)\tn No Mutations\tn Minor Subclone\tn Multiple Mutations\tn Loss of Heterozygosity\tn nonsense mediated decay\tn Reverse ASE\tn Single\tn ASE" +
                "\tfrac No Mutations\tfrac Minor Subclone\tfrac Multiple Mutations\tfrac Loss of Heterozygosity\tfrac nonsense mediated decay\tfrac Reverse ASE\tfrac Single\tfrac ASE" +
                "\tfrac of all mutant multiple mutations\tfrac of all mutant loss of heterozygosity\tfrac of all mutant nonsense mediated decay\tfrac of all mutant reverse ASE\tfrac of all mutant single\tfrac of all mutant ASE" +
                "\tgraph title\theading1\theading2\theading3\theading4\theading5\theading6\theading7\theading8");

            foreach (var result in results)
            {
                outputFile.WriteLine(ASETools.ConvertToExcelString(result.hugo_symbol + " (n = " + result.totalMutant() + ")") + "\t" + result.nTooFewReads + "\t" + result.totalEvaluated() + "\t" + result.totalMutant() +
                    "\t" + result.nZero + "\t" + result.nMinorSubclone + "\t" + result.nMultiple + "\t" + result.nLossOfHeterozygosity + "\t" + result.nNonsenseMediatedDecay + "\t" + result.nOnePlusReverseASE + "\t" + result.nSingle + "\t" + result.nOnePlusASE +
                    "\t" + result.fracZero() + "\t" + result.fracMinorSubclone() + "\t" + result.fracMultiple() + "\t" + result.fracLossOfHeterozygosity() + "\t" + result.fracNonsenseMediatedDecay() + "\t" + result.fracOnePlusReverseASE() + "\t" + result.fracSingle() + "\t" + result.fracOnePlusASE() +
                    "\t" + result.fracOfAllMutantMultiple() + "\t" + result.fracOfAllMutantLossOfHeterozygosity() + "\t" + result.fracOfAllMutantNonsenseMediatedDecay() + "\t" + result.fracOfAllMutantOnePlusReverseASE() + "\t" + result.fracOfAllMutantSingle() + "\t" + result.fracOfAllMutantOnePlusASE() +
                    "\tBreakdown of " + result.hugo_symbol + " mutant tumors excluding minor subclones\tNo mutations\tMinor subclone\tMultiple mutations\tLoss of heterozygosity\tNonsense mediated decay\tVAF < 0.4\t0.4 <= VAF <= 0.6\tVAF > 0.6");                    
            }

#if false
            outputFile.WriteLine("Hugo Symbol\tn Too Few Reads\tn Evaluated\tn Mutant (ignoring minor subclones)\tn No Mutations\tfrac No Mutations\tn Minor Subclone\tfrac Minor Subclone\tn Multiple Mutations\tfrac Multiple Mutations\tfrac of all mutant multiple mutations"
                + "\tn Loss of Heterozygosity\tfrac Loss of Heterozygosity\tfrac of all mutant loss of heterozygosity"
                + "\tn nonsense mediated decay\tfrac nonsense mediated decay\tfrac of all mutant nonsense medidated decay"
                + "\tn Reverse ASE\tfrac Reverse ASE\tfrac of all mutant reverse ASE\tn ASE\tfrac ASE\tfrac of all mutant ASE\tn Single\tfrac Single\tfrac of all mutant single\tgraph title\theading1\theading2\theading3\theading4\theading5\theading6\theading7\theading8");

            foreach (var result in results)
            {
                outputFile.WriteLine(ASETools.ConvertToExcelString(result.hugo_symbol) + "\t" + result.nTooFewReads + "\t" + result.totalEvaluated() + "\t" + result.totalMutant()
                    + "\t" + result.nZero + "\t" + result.fracZero()
                    + "\t" + result.nMinorSubclone + "\t" + result.fracMinorSubclone()
                    + "\t" + result.nMultiple + "\t" + result.fracMultiple() + "\t" + result.fracOfAllMutantMultiple()
                    + "\t" + result.nLossOfHeterozygosity + "\t" + result.fracLossOfHeterozygosity() +"\t" + result.fracOfAllMutantLossOfHeterozygosity()
                    + "\t" + result.nNonsenseMediatedDecay + "\t" + result.fracNonsenseMediatedDecay() + "\t" + result.fracOfAllMutantNonsenseMediatedDecay()
                    + "\t" + result.nOnePlusReverseASE + "\t" + result.fracOnePlusReverseASE() + "\t" + result.fracOfAllMutantOnePlusReverseASE()
                    + "\t" + result.nOnePlusASE + "\t" + result.fracOnePlusASE() + "\t" + result.fracOfAllMutantOnePlusASE()
                    + "\t" + result.nSingle + "\t" + result.fracSingle() + "\t" + result.fracOfAllMutantSingle()
                    + "\tBreakdown of " + result.hugo_symbol + " mutant tumors excluding minor subclones\tNo mutations\tMinor subclone\tMultiple mutations\tLoss of heterozygosity\tNonsense mediated decay\tVAF < 0.4\tVAF > 0.6\t0.4 <= VAF <= 0.6"
                    );
            }
#endif // false

            outputFile.Close();

            Console.WriteLine(ASETools.ElapsedTimeInSeconds(stopwatch));

        } // main
    }
}
