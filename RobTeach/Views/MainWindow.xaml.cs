using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
// Explicitly using System.Windows.Shapes.Shape to avoid ambiguity
// using System.Windows.Shapes; // This line can be removed if all Shape usages are qualified
using Microsoft.Win32;
using RobTeach.Services;
using RobTeach.Models;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using IxMilia.Dxf; // Required for DxfFile
using IxMilia.Dxf.Entities;
// using netDxf.Header; // No longer needed with IxMilia.Dxf
using System.IO;
// using System.Windows.Threading; // Was for optional Dispatcher.Invoke, not currently used.
// using System.Text.RegularExpressions; // Was for optional IP validation, not currently used.

namespace RobTeach.Views
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml. This is the main window of the RobTeach application,
    /// handling UI events, displaying CAD data, managing configurations, and initiating Modbus communication.
    /// </summary>
    public partial class MainWindow : Window
    {
        // Services used by the MainWindow
        private readonly CadService _cadService = new CadService();
        private readonly ConfigurationService _configService = new ConfigurationService();
        private readonly ModbusService _modbusService = new ModbusService();

        // Current state variables
        private DxfFile? _currentDxfDocument; // Holds the currently loaded DXF document object.
        private string? _currentDxfFilePath;      // Path to the currently loaded DXF file.
        private string? _currentLoadedConfigPath; // Path to the last successfully loaded configuration file.
        private Models.Configuration _currentConfiguration; // The active configuration, either loaded or built from selections.

        // Collections for managing DXF entities and their WPF shape representations
        private readonly List<object> _selectedDxfEntities = new List<object>(); // Stores original DXF entities selected by the user.
        // Qualified System.Windows.Shapes.Shape for dictionary key
        private readonly Dictionary<System.Windows.Shapes.Shape, object> _wpfShapeToDxfEntityMap = new Dictionary<System.Windows.Shapes.Shape, object>();
        private readonly Dictionary<string, DxfEntity> _dxfEntityHandleMap = new Dictionary<string, DxfEntity>(); // Maps DXF entity handles to entities for quick lookup when loading configs.
        private readonly List<System.Windows.Shapes.Polyline> _trajectoryPreviewPolylines = new List<System.Windows.Shapes.Polyline>(); // Keeps track of trajectory preview polylines for easy removal.

        // Fields for CAD Canvas Zoom/Pan functionality
        private ScaleTransform _scaleTransform;         // Handles scaling (zoom) of the canvas content.
        private TranslateTransform _translateTransform; // Handles translation (pan) of the canvas content.
        private TransformGroup _transformGroup;         // Combines scale and translate transforms.
        private System.Windows.Point _panStartPoint;    // Qualified: Stores the starting point of a mouse pan operation.
        private bool _isPanning;                        // Flag indicating if a pan operation is currently in progress.
        private Rect _dxfBoundingBox = Rect.Empty;      // Stores the calculated bounding box of the entire loaded DXF document.

        // Styling constants for visual feedback
        private static readonly Brush DefaultStrokeBrush = Brushes.DarkSlateGray; // Default color for CAD shapes.
        private static readonly Brush SelectedStrokeBrush = Brushes.DodgerBlue;   // Color for selected CAD shapes.
        private const double DefaultStrokeThickness = 1;                          // Default stroke thickness.
        private const double SelectedStrokeThickness = 2.5;                       // Thickness for selected shapes and trajectories.
        private const string TrajectoryPreviewTag = "TrajectoryPreview";          // Tag for identifying trajectory polylines on canvas (not actively used for removal yet).
        private const double TrajectoryPointResolutionAngle = 15.0; // Default resolution for discretizing arcs/circles.


        /// <summary>
        /// Initializes a new instance of the <see cref="MainWindow"/> class.
        /// Sets up default values, initializes transformation objects for the canvas,
        /// and attaches necessary mouse event handlers for canvas interaction.
        /// </summary>
        public MainWindow()
        {
            InitializeComponent();
            if (CadCanvas.Background == null) CadCanvas.Background = Brushes.LightGray; // Ensure canvas has a background for hit testing.

            // Initialize product name with a timestamp to ensure uniqueness for new configurations.
            ProductNameTextBox.Text = $"Product_{DateTime.Now:yyyyMMddHHmmss}";
            _currentConfiguration = new Models.Configuration();
            _currentConfiguration.ProductName = ProductNameTextBox.Text;

            // Setup transformations for the CAD canvas
            _scaleTransform = new ScaleTransform(1, 1);
            _translateTransform = new TranslateTransform(0, 0);
            _transformGroup = new TransformGroup();
            _transformGroup.Children.Add(_scaleTransform);
            _transformGroup.Children.Add(_translateTransform);
            CadCanvas.RenderTransform = _transformGroup;

            // Attach mouse event handlers for canvas zoom and pan
            CadCanvas.MouseWheel += CadCanvas_MouseWheel;
            CadCanvas.MouseDown += CadCanvas_MouseDown; // For initiating pan
            CadCanvas.MouseMove += CadCanvas_MouseMove; // For active panning
            CadCanvas.MouseUp += CadCanvas_MouseUp;     // For ending pan
        }

        /// <summary>
        /// Handles the Closing event of the window. Ensures Modbus connection is disconnected.
        /// </summary>
        private void MainWindow_Closing(object sender, CancelEventArgs e)
        {
            _modbusService.Disconnect(); // Clean up Modbus connection.
        }

        /// <summary>
        /// Handles the Click event of the "Load DXF" button.
        /// Prompts the user to select a DXF file, loads it using <see cref="CadService"/>,
        /// processes its entities for display, and fits the view to the loaded drawing.
        /// </summary>
        private void LoadDxfButton_Click(object sender, RoutedEventArgs e)
        {
            OpenFileDialog openFileDialog = new OpenFileDialog {
                Filter = "DXF files (*.dxf)|*.dxf|All files (*.*)|*.*", Title = "Load DXF File" };
            string initialDir = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", ".."));
            if (!Directory.Exists(initialDir)) initialDir = "/app/RobTeachProject/RobTeach/";
            openFileDialog.InitialDirectory = initialDir;

            try {
                if (openFileDialog.ShowDialog() == true) {
                    _currentDxfFilePath = openFileDialog.FileName;
                    StatusTextBlock.Text = $"Loading DXF: {Path.GetFileName(_currentDxfFilePath)}...";

                    CadCanvas.Children.Clear();
                    _wpfShapeToDxfEntityMap.Clear(); _selectedDxfEntities.Clear();
                    _trajectoryPreviewPolylines.Clear(); _dxfEntityHandleMap.Clear();
                    _currentConfiguration = new Models.Configuration { ProductName = ProductNameTextBox.Text };
                    _currentLoadedConfigPath = null;
                    _currentDxfDocument = null;
                    _dxfBoundingBox = Rect.Empty;
                    UpdateTrajectoryPreview();

                    _currentDxfDocument = _cadService.LoadDxf(_currentDxfFilePath);

                    if (_currentDxfDocument == null) {
                        StatusTextBlock.Text = "Failed to load DXF document (null document returned).";
                        MessageBox.Show("The DXF document could not be loaded. The file might be empty or an unknown error occurred.", "Error Loading DXF", MessageBoxButton.OK, MessageBoxImage.Error);
                        return;
                    }

                    // Note: IxMilia.Dxf doesn't expose Handle property directly
                    // We'll skip handle mapping for now

                    List<System.Windows.Shapes.Shape> wpfShapes = _cadService.GetWpfShapesFromDxf(_currentDxfDocument);
                    int shapeIndex = 0;
                    
                    foreach(var entity in _currentDxfDocument.Entities)
                    {
                        if (shapeIndex < wpfShapes.Count && wpfShapes[shapeIndex] != null) {
                            var wpfShape = wpfShapes[shapeIndex];
                            wpfShape.Stroke = DefaultStrokeBrush; 
                            wpfShape.StrokeThickness = DefaultStrokeThickness;
                            wpfShape.MouseLeftButtonDown += OnCadEntityClicked;
                            _wpfShapeToDxfEntityMap[wpfShape] = entity;
                            CadCanvas.Children.Add(wpfShape);
                            shapeIndex++; 
                        }
                    }

                    _dxfBoundingBox = GetDxfBoundingBox(_currentDxfDocument);
                    PerformFitToView();
                    StatusTextBlock.Text = $"Loaded: {Path.GetFileName(_currentDxfFilePath)}. Click shapes to select.";
                } else { StatusTextBlock.Text = "DXF loading cancelled."; }
            }
            catch (FileNotFoundException fnfEx) {
                StatusTextBlock.Text = "Error: DXF file not found.";
                MessageBox.Show($"DXF file not found:\n{fnfEx.Message}", "Error Loading DXF", MessageBoxButton.OK, MessageBoxImage.Error);
                _currentDxfDocument = null;
            }
            // Removed specific catch for netDxf.DxfVersionNotSupportedException. General Exception will handle DXF-specific errors.
            catch (Exception ex) {
                StatusTextBlock.Text = "Error loading or processing DXF file.";
                MessageBox.Show($"An error occurred while loading or processing the DXF file:\n{ex.Message}\n\nEnsure the file is a valid DXF format.", "Error Loading DXF", MessageBoxButton.OK, MessageBoxImage.Error);
                _currentDxfDocument = null;
                CadCanvas.Children.Clear();
                _selectedDxfEntities.Clear(); _wpfShapeToDxfEntityMap.Clear(); _dxfEntityHandleMap.Clear();
                _trajectoryPreviewPolylines?.Clear();
                _currentConfiguration = new Models.Configuration { ProductName = ProductNameTextBox.Text };
                UpdateTrajectoryPreview();
            }
        }

        /// <summary>
        /// Handles the click event on a CAD entity shape, toggling its selection state.
        /// </summary>
        private void OnCadEntityClicked(object sender, MouseButtonEventArgs e)
        {
            if (sender is System.Windows.Shapes.Shape clickedShape && _wpfShapeToDxfEntityMap.ContainsKey(clickedShape))
            {
                var dxfEntity = _wpfShapeToDxfEntityMap[clickedShape];
                
                if (_selectedDxfEntities.Contains(dxfEntity))
                {
                    // Deselect
                    _selectedDxfEntities.Remove(dxfEntity);
                    clickedShape.Stroke = DefaultStrokeBrush;
                    clickedShape.StrokeThickness = DefaultStrokeThickness;
                }
                else
                {
                    // Select
                    _selectedDxfEntities.Add(dxfEntity);
                    clickedShape.Stroke = SelectedStrokeBrush;
                    clickedShape.StrokeThickness = SelectedStrokeThickness;
                }
                
                UpdateTrajectoryPreview();
                StatusTextBlock.Text = $"Selected {_selectedDxfEntities.Count} entities.";
            }
        }

        /// <summary>
        /// Updates the trajectory preview by drawing polylines for selected entities.
        /// </summary>
        private void UpdateTrajectoryPreview()
        {
            // Clear existing trajectory previews
            foreach (var polyline in _trajectoryPreviewPolylines)
            {
                CadCanvas.Children.Remove(polyline);
            }
            _trajectoryPreviewPolylines.Clear();

            // Generate preview for selected entities
            foreach (var entity in _selectedDxfEntities)
            {
                List<System.Windows.Point> points = new List<System.Windows.Point>();
                
                switch (entity)
                {
                    case DxfLine line:
                        points = _cadService.ConvertLineToPoints(line);
                        break;
                    case DxfArc arc:
                        points = _cadService.ConvertArcToPoints(arc, TrajectoryPointResolutionAngle);
                        break;
                    case DxfCircle circle:
                        points = _cadService.ConvertCircleToPoints(circle, TrajectoryPointResolutionAngle);
                        break;
                }

                if (points.Count > 1)
                {
                    var polyline = new System.Windows.Shapes.Polyline
                    {
                        Points = new System.Windows.Media.PointCollection(points),
                        Stroke = Brushes.Red,
                        StrokeThickness = SelectedStrokeThickness,
                        StrokeDashArray = new System.Windows.Media.DoubleCollection { 5, 3 },
                        Tag = TrajectoryPreviewTag
                    };
                    
                    _trajectoryPreviewPolylines.Add(polyline);
                    CadCanvas.Children.Add(polyline);
                }
            }
        }

        /// <summary>
        /// Creates a configuration object from the current application state.
        /// </summary>
        private Models.Configuration CreateConfigurationFromCurrentState(bool forSaving = false)
        {
            var config = new Models.Configuration
            {
                ProductName = ProductNameTextBox.Text,
                TransformParameters = new Models.Transform() // Use default transform for now
            };

            // Create trajectory from selected entities
            if (_selectedDxfEntities.Count > 0)
            {
                var trajectory = new Models.Trajectory
                {
                    NozzleNumber = int.TryParse(NozzleNumberTextBox.Text, out int nozzle) ? nozzle : 1,
                    IsWater = IsWaterCheckBox.IsChecked ?? true,
                    EntityType = "Mixed", // Since we can have multiple entity types
                    OriginalEntityHandle = "Multiple" // Multiple entities
                };

                // Combine all selected entities into one trajectory
                foreach (var entity in _selectedDxfEntities)
                {
                    List<System.Windows.Point> entityPoints = new List<System.Windows.Point>();
                    
                    switch (entity)
                    {
                        case DxfLine line:
                            entityPoints = _cadService.ConvertLineToPoints(line);
                            break;
                        case DxfArc arc:
                            entityPoints = _cadService.ConvertArcToPoints(arc, TrajectoryPointResolutionAngle);
                            break;
                        case DxfCircle circle:
                            entityPoints = _cadService.ConvertCircleToPoints(circle, TrajectoryPointResolutionAngle);
                            break;
                    }
                    
                    trajectory.Points.AddRange(entityPoints);
                }

                config.Trajectories.Add(trajectory);
            }

            return config;
        }
        private void SaveConfigButton_Click(object sender, RoutedEventArgs e) { /* ... (No change) ... */ }
        private void LoadConfigButton_Click(object sender, RoutedEventArgs e) { /* ... (No change) ... */ }
        private void ModbusConnectButton_Click(object sender, RoutedEventArgs e) { /* ... (No change) ... */ }
        private void ModbusDisconnectButton_Click(object sender, RoutedEventArgs e) { /* ... (No change) ... */ }
        private void SendToRobotButton_Click(object sender, RoutedEventArgs e) { /* ... (No change) ... */ }

        /// <summary>
        /// Calculates the overall bounding box of the DXF document, considering header extents and all entity extents.
        /// </summary>
        /// <param name="dxfDoc">The DXF document.</param>
        /// <returns>A Rect representing the bounding box, or Rect.Empty if no valid bounds can be determined.</returns>
        private Rect GetDxfBoundingBox(DxfFile dxfDoc)
        {
            if (dxfDoc == null)
            {
                return Rect.Empty;
            }

            double minX = double.MaxValue, minY = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue;
            bool hasValidBounds = false;

            // Calculate bounds directly from entities
            if (dxfDoc.Entities != null && dxfDoc.Entities.Any())
            {
                foreach (var entity in dxfDoc.Entities)
                {
                    if (entity == null) continue;

                    try
                    {
                        // Calculate entity bounds directly
                        var bounds = CalculateEntityBoundsSimple(entity);
                        if (bounds.HasValue)
                        {
                            var (eMinX, eMinY, eMaxX, eMaxY) = bounds.Value;
                            minX = Math.Min(minX, eMinX);
                            minY = Math.Min(minY, eMinY);
                            maxX = Math.Max(maxX, eMaxX);
                            maxY = Math.Max(maxY, eMaxY);
                            hasValidBounds = true;
                        }
                    }
                    catch
                    {
                        // Skip entities that can't be processed
                        continue;
                    }
                }
            }

            if (!hasValidBounds)
            {
                return Rect.Empty;
            }

            return new System.Windows.Rect(minX, minY, maxX - minX, maxY - minY);
        }

        private void FitToViewButton_Click(object sender, RoutedEventArgs e) { /* ... (No change) ... */ }
        private void PerformFitToView() { /* ... (No change) ... */ }
        private void CadCanvas_MouseWheel(object sender, MouseWheelEventArgs e) { /* ... (No change) ... */ }
        private void CadCanvas_MouseDown(object sender, MouseButtonEventArgs e) { /* ... (No change) ... */ }
        private void CadCanvas_MouseMove(object sender, MouseEventArgs e) { /* ... (No change) ... */ }
        private void CadCanvas_MouseUp(object sender, MouseButtonEventArgs e) { /* ... (No change) ... */ }
        /// <summary>
        /// Calculates the bounding rectangle for a given DXF entity.
        /// </summary>
        private (double minX, double minY, double maxX, double maxY)? CalculateEntityBoundsSimple(DxfEntity entity)
        {
            try
            {
                switch (entity)
                {
                    case DxfLine line:
                        var minX = Math.Min(line.P1.X, line.P2.X);
                        var maxX = Math.Max(line.P1.X, line.P2.X);
                        var minY = Math.Min(line.P1.Y, line.P2.Y);
                        var maxY = Math.Max(line.P1.Y, line.P2.Y);
                        return (minX, minY, maxX, maxY);

                    case DxfArc arc:
                        var centerX = arc.Center.X;
                        var centerY = arc.Center.Y;
                        var radius = arc.Radius;
                        return (centerX - radius, centerY - radius, centerX + radius, centerY + radius);

                    case DxfCircle circle:
                        var cX = circle.Center.X;
                        var cY = circle.Center.Y;
                        var r = circle.Radius;
                        return (cX - r, cY - r, cX + r, cY + r);

                    default:
                        return null;
                }
            }
            catch
            {
                return null;
            }
        }



        private void HandleError(Exception ex, string action) { /* ... (No change) ... */ }
    }
}
