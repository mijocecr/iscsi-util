using System;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using System.Globalization;

namespace ISCSI_Util.Converters;

/// <summary>
/// Converts iSCSI target state to a circle geometry for the UI indicator.
/// Always returns the circle geometry defined in App.axaml.
/// </summary>
public class EstadoToGeometryConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
            // Always return the circle geometry from resources
            return Application.Current?.Resources["CircleGeometry"] as Geometry;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    /// <summary>
    /// Converts a boolean connection state to a color (green if connected, transparent if not).
    /// Used to display the connection status in the UI.
    /// </summary>
    public class BoolToColorConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            bool conectado = value is bool b && b;
            return conectado ? Brushes.Green : Brushes.Transparent;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    /// <summary>
    /// Converts a boolean connection state to stroke thickness (0 if connected, 2 if not).
    /// Used to display the border visibility of the connection indicator.
    /// </summary>
    public class BoolToStrokeConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            bool conectado = value is bool b && b;
            return conectado ? 0 : 2;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }
