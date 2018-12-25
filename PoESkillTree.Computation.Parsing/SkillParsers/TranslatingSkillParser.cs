﻿using System;
using System.Collections.Generic;
using System.Linq;
using MoreLinq;
using PoESkillTree.Computation.Common;
using PoESkillTree.Computation.Common.Builders;
using PoESkillTree.Computation.Common.Builders.Conditions;
using PoESkillTree.Computation.Common.Builders.Stats;
using PoESkillTree.GameModel;
using PoESkillTree.GameModel.Skills;
using PoESkillTree.GameModel.StatTranslation;
using PoESkillTree.Utils.Extensions;

namespace PoESkillTree.Computation.Parsing.SkillParsers
{
    public delegate IParser<UntranslatedStatParserParameter> UntranslatedStatParserFactory(
        string statTranslationFileName);

    public class TranslatingSkillParser
    {
        private readonly IBuilderFactories _builderFactories;
        private readonly IMetaStatBuilders _metaStatBuilders;
        private readonly UntranslatedStatParserFactory _statParserFactory;

        private SkillPreParseResult _preParseResult;
        private IEnumerable<UntranslatedStat> _parsedStats;

        public TranslatingSkillParser(
            IBuilderFactories builderFactories, IMetaStatBuilders metaStatBuilders,
            UntranslatedStatParserFactory statParserFactory)
            => (_builderFactories, _metaStatBuilders, _statParserFactory) =
                (builderFactories, metaStatBuilders, statParserFactory);

        public ParseResult Parse(
            Skill skill, SkillPreParseResult preParseResult, PartialSkillParseResult partialResult)
        {
            _preParseResult = preParseResult;
            _parsedStats = partialResult.ParsedStats.ToHashSet();

            var isMainSkill = preParseResult.IsMainSkill;
            var isActiveSkill = _metaStatBuilders.IsActiveSkill(skill);
            var level = preParseResult.LevelDefinition;
            var qualityStats = level.QualityStats.Select(s => ApplyQuality(s, skill));
            var parseResults = new List<ParseResult>
            {
                ParseResult.Success(partialResult.ParsedModifiers.ToList()),
                TranslateAndParse(qualityStats, isMainSkill),
                TranslateAndParse(level.Stats, isMainSkill)
            };

            foreach (var (partIndex, stats) in level.AdditionalStatsPerPart.Index())
            {
                var condition = isMainSkill.And(_builderFactories.StatBuilders.MainSkillPart.Value.Eq(partIndex));
                var result = TranslateAndParse(stats, condition);
                parseResults.Add(result);
            }

            var qualityBuffStats =
                level.QualityBuffStats.Select(s => new BuffStat(ApplyQuality(s.Stat, skill), s.AffectedEntities));
            parseResults.Add(TranslateAndParseBuff(qualityBuffStats, isActiveSkill));
            parseResults.Add(TranslateAndParseBuff(level.BuffStats, isActiveSkill));

            var qualityPassiveStats = level.QualityPassiveStats.Select(s => ApplyQuality(s, skill));
            parseResults.Add(TranslateAndParse(qualityPassiveStats, isActiveSkill));
            parseResults.Add(TranslateAndParse(level.PassiveStats, isActiveSkill));

            _preParseResult = null;
            _parsedStats = null;
            return ParseResult.Aggregate(parseResults);
        }

        private ParseResult TranslateAndParse(IEnumerable<UntranslatedStat> stats, IConditionBuilder condition)
        {
            var result = TranslateAndParse(_preParseResult.SkillDefinition.StatTranslationFile,
                _preParseResult.LocalSource, stats);
            return result.ApplyCondition(condition.Build);
        }

        private ParseResult TranslateAndParseBuff(IEnumerable<BuffStat> buffStats, IConditionBuilder condition)
        {
            var results = new List<ParseResult>();
            var buffBuilder = _builderFactories.SkillBuilders.FromId(_preParseResult.SkillDefinition.Id).Buff;
            foreach (var (stat, affectedEntities) in buffStats)
            {
                foreach (var affectedEntity in affectedEntities)
                {
                    var result = Parse(StatTranslationLoader.MainFileName, stat, affectedEntity);
                    if (result.SuccessfullyParsed && result.Modifiers.IsEmpty())
                        result = Parse(StatTranslationLoader.SkillFileName, stat, affectedEntity);
                    result = result.ApplyCondition(condition.Build);

                    var buildParameters = new BuildParameters(_preParseResult.GlobalSource, affectedEntity, default);
                    var multiplier = buffBuilder.BuildAddStatMultiplier(buildParameters, new[] { Entity.Character });
                    result = result.ApplyMultiplier(_ => multiplier);
                    results.Add(result);
                }
            }

            return ParseResult.Aggregate(results);

            ParseResult Parse(string statTranslationFileName, UntranslatedStat stat, Entity affectedEntity)
                => TranslateAndParse(statTranslationFileName, _preParseResult.LocalSource, new[] { stat },
                    affectedEntity);
        }

        private ParseResult TranslateAndParse(
            string statTranslationFileName,
            ModifierSource.Local.Skill localModifierSource,
            IEnumerable<UntranslatedStat> stats,
            Entity modifierSourceEntity = Entity.Character)
        {
            var unparsedStats = stats.Except(_parsedStats).ToList();
            var statParser = _statParserFactory(statTranslationFileName);
            return statParser.Parse(localModifierSource, modifierSourceEntity, unparsedStats);
        }

        private static UntranslatedStat ApplyQuality(UntranslatedStat qualityStat, Skill skill)
            => new UntranslatedStat(qualityStat.StatId, qualityStat.Value * skill.Quality / 1000);
    }
}