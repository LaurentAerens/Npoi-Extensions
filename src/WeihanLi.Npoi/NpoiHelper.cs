﻿using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Reflection;
using JetBrains.Annotations;
using NPOI.SS.UserModel;
using NPOI.SS.Util;
using WeihanLi.Extensions;
using WeihanLi.Npoi.Configurations;
using WeihanLi.Npoi.Settings;

namespace WeihanLi.Npoi
{
    internal class NpoiHelper<TEntity> where TEntity : new()
    {
        private readonly IReadOnlyList<SheetSetting> _sheetSettings;
        private readonly IDictionary<PropertyInfo, PropertySetting> _propertyColumnDictionary;
        private readonly ExcelConfiguration<TEntity> _excelConfiguration;
        private readonly Type _entityType;

        internal NpoiHelper()
        {
            _entityType = typeof(TEntity);
            _excelConfiguration = (ExcelConfiguration<TEntity>)InternalCache.TypeExcelConfigurationDictionary.GetOrAdd(_entityType,
                t => InternalHelper.GetExcelConfigurationMapping<TEntity>());

            _sheetSettings = _excelConfiguration.SheetSettings.AsReadOnly();

            InternalHelper.AdjustColumnIndex(_excelConfiguration);

            _propertyColumnDictionary = _excelConfiguration.PropertyConfigurationDictionary.Where(_ => !_.Value.PropertySetting.IsIgnored).ToDictionary(_ => _.Key, _ => _.Value.PropertySetting);
        }

        public List<TEntity> SheetToEntityList([NotNull]ISheet sheet, int sheetIndex)
        {
            var sheetSetting = sheetIndex >= 0 && sheetIndex < _sheetSettings.Count ? _sheetSettings[sheetIndex] : _sheetSettings[0];

            var entities = new List<TEntity>(sheet.PhysicalNumberOfRows - sheetSetting.StartRowIndex);

            foreach (var row in sheet.GetRowCollection())
            {
                if (row.RowNum == sheetSetting.HeaderRowIndex) //读取Header
                {
                    for (var i = 0; i < row.Cells.Count; i++)
                    {
                        if (row.GetCell(i) == null)
                        {
                            continue;
                        }
                        var col = _propertyColumnDictionary.GetPropertySetting(row.GetCell(i).StringCellValue.Trim());
                        if (null != col)
                        {
                            col.ColumnIndex = i;
                        }
                    }
                }
                else if (row.RowNum >= sheetSetting.StartRowIndex)
                {
                    var entity = new TEntity();
                    if (typeof(TEntity).IsValueType)
                    {
                        var obj = (object)entity;// boxing for value types
                        foreach (var key in _propertyColumnDictionary.Keys)
                        {
                            var colIndex = _propertyColumnDictionary[key].ColumnIndex;
                            key.GetValueSetter().Invoke(obj, row.GetCell(colIndex).GetCellValue(key.PropertyType));
                        }
                        entity = (TEntity)obj;// unboxing
                    }
                    else
                    {
                        foreach (var key in _propertyColumnDictionary.Keys)
                        {
                            var colIndex = _propertyColumnDictionary[key].ColumnIndex;
                            key.GetValueSetter().Invoke(entity, row.GetCell(colIndex).GetCellValue(key.PropertyType));
                        }
                    }
                    entities.Add(entity);
                }
            }
            return entities;
        }

        public ISheet DataTableToSheet([NotNull]ISheet sheet, DataTable dataTable, int sheetIndex)
        {
            if (null == dataTable || dataTable.Rows.Count == 0 || dataTable.Columns.Count == 0 || _propertyColumnDictionary.Keys.Count == 0)
            {
                return sheet;
            }
            var headerRow = sheet.CreateRow(0);
            for (var i = 0; i < dataTable.Columns.Count; i++)
            {
                var col = _propertyColumnDictionary.GetPropertySettingByPropertyName(dataTable.Columns[i].ColumnName);
                if (null != col)
                {
                    headerRow.CreateCell(col.ColumnIndex).SetCellValue(col.ColumnTitle);
                }
            }

            var sheetSetting = sheetIndex >= 0 && sheetIndex < _sheetSettings.Count ? _sheetSettings[sheetIndex] : _sheetSettings[0];

            for (var i = 0; i < dataTable.Rows.Count; i++)
            {
                var row = sheet.CreateRow(sheetSetting.StartRowIndex + i);
                for (var j = 0; j < dataTable.Columns.Count; j++)
                {
                    var col = _propertyColumnDictionary.GetPropertySettingByPropertyName(dataTable.Columns[j].ColumnName);
                    row.CreateCell(col.ColumnIndex).SetCellValue(dataTable.Rows[i][j], col.ColumnFormatter);
                }
            }

            // autosizecolumn
            foreach (var setting in _propertyColumnDictionary.Values)
            {
                sheet.AutoSizeColumn(setting.ColumnIndex);
            }

            foreach (var freezeSetting in _excelConfiguration.FreezeSettings)
            {
                sheet.CreateFreezePane(freezeSetting.ColSplit, freezeSetting.RowSplit, freezeSetting.LeftMostColumn, freezeSetting.TopRow);
            }

            if (_excelConfiguration.FilterSetting != null)
            {
                sheet.SetAutoFilter(new CellRangeAddress(sheetSetting.HeaderRowIndex, dataTable.Rows.Count + sheetSetting.HeaderRowIndex, _excelConfiguration.FilterSetting.FirstColumn, _excelConfiguration.FilterSetting.LastColumn ?? _propertyColumnDictionary.Values.Max(_ => _.ColumnIndex)));
            }

            return sheet;
        }

        public ISheet EntityListToSheet([NotNull]ISheet sheet, IReadOnlyList<TEntity> entityList, int sheetIndex)
        {
            if (null == entityList || entityList.Count == 0 || _propertyColumnDictionary.Keys.Count == 0)
            {
                return sheet;
            }
            var sheetSetting = sheetIndex >= 0 && sheetIndex < _sheetSettings.Count ? _sheetSettings[sheetIndex] : _sheetSettings[0];

            var headerRow = sheet.CreateRow(sheetSetting.HeaderRowIndex);
            foreach (var key in _propertyColumnDictionary.Keys)
            {
                headerRow.CreateCell(_propertyColumnDictionary[key].ColumnIndex).SetCellValue(_propertyColumnDictionary[key].ColumnTitle);
            }

            for (var i = 0; i < entityList.Count; i++)
            {
                var row = sheet.CreateRow(sheetSetting.StartRowIndex + i);
                foreach (var key in _propertyColumnDictionary.Keys)
                {
                    var propertyValue = key.GetValueGetter<TEntity>().Invoke(entityList[i]);

                    var formatterFunc = InternalCache.ColumnFormatterFuncCache.GetOrAdd(key, p =>
                    {
                        var propertyType = typeof(PropertySetting<,>).MakeGenericType(_entityType, p.PropertyType);
                        return propertyType.GetProperty("ColumnFormatterFunc")?.GetValueGetter().Invoke(_propertyColumnDictionary[key]);
                    });

                    if (null != formatterFunc)
                    {
                        var funcType = typeof(Func<,,>).MakeGenericType(_entityType, key.PropertyType, typeof(object));
                        var method = funcType.GetProperty("Method")?.GetValueGetter().Invoke(formatterFunc) as MethodInfo;
                        var target = funcType.GetProperty("Target")?.GetValueGetter().Invoke(formatterFunc);

                        if (null != method && target != null)
                        {
                            // apply custom formatterFunc
                            propertyValue = method.Invoke(target, new[] { entityList[i], propertyValue });
                        }
                    }

                    row.CreateCell(_propertyColumnDictionary[key].ColumnIndex).SetCellValue(propertyValue, _propertyColumnDictionary[key].ColumnFormatter);
                }
            }

            // AutoSizeColumn
            foreach (var setting in _propertyColumnDictionary.Values)
            {
                sheet.AutoSizeColumn(setting.ColumnIndex);
            }

            foreach (var freezeSetting in _excelConfiguration.FreezeSettings)
            {
                sheet.CreateFreezePane(freezeSetting.ColSplit, freezeSetting.RowSplit, freezeSetting.LeftMostColumn, freezeSetting.TopRow);
            }

            if (_excelConfiguration.FilterSetting != null)
            {
                sheet.SetAutoFilter(new CellRangeAddress(sheetSetting.HeaderRowIndex, entityList.Count + sheetSetting.HeaderRowIndex, _excelConfiguration.FilterSetting.FirstColumn, _excelConfiguration.FilterSetting.LastColumn ?? _propertyColumnDictionary.Values.Max(_ => _.ColumnIndex)));
            }

            return sheet;
        }
    }
}
