using IxMilia.Dxf;
using IxMilia.Dxf.Entities;
// using netDxf.Tables;
// using netDxf.Units;
using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Linq;
// using System.Windows.Shapes;

namespace RobTeach.Services
{
    /// <summary>
    /// Provides services for loading CAD (DXF) files and converting DXF entities
    /// into WPF shapes and trajectory points using IxMilia.Dxf library.
    /// </summary>
    public class CadService
    {
        /// <summary>
        /// Loads a DXF document from the specified file path with enhanced error handling and version compatibility.
        /// </summary>
        public DxfFile LoadDxf(string filePath)
        {
            if (string.IsNullOrEmpty(filePath))
            {
                throw new ArgumentNullException(nameof(filePath), "File path cannot be null or empty.");
            }
            if (!File.Exists(filePath))
            {
                throw new FileNotFoundException("DXF file not found.", filePath);
            }
            
            try
            {
                // IxMilia.Dxf is more forgiving with DXF formats
                DxfFile dxf = DxfFile.Load(filePath);
                return dxf;
            }
            catch (Exception ex)
            {
                throw new Exception($"Error loading or parsing DXF file: {ex.Message}", ex);
            }
        }
        
        /// <summary>
        /// Converts entities from a <see cref="DxfFile"/> into a list of WPF <see cref="System.Windows.Shapes.Shape"/> objects for display.
        /// Supports Lines, Arcs, and Circles.
        /// </summary>
        public List<System.Windows.Shapes.Shape> GetWpfShapesFromDxf(DxfFile dxfFile)
        {
            var wpfShapes = new List<System.Windows.Shapes.Shape>();
            if (dxfFile == null) return wpfShapes;

            // Process all entities
            foreach (var entity in dxfFile.Entities)
            {
                switch (entity)
                {
                    case DxfLine dxfLine:
                        var wpfLine = new System.Windows.Shapes.Line
                        {
                            X1 = dxfLine.P1.X, Y1 = dxfLine.P1.Y,
                            X2 = dxfLine.P2.X, Y2 = dxfLine.P2.Y,
                            IsHitTestVisible = true
                        };
                        wpfShapes.Add(wpfLine);
                        break;

                    case DxfArc dxfArc:
                        var arcPath = CreateArcPath(dxfArc);
                        if (arcPath != null)
                        {
                            wpfShapes.Add(arcPath);
                        }
                        break;

                    case DxfCircle dxfCircle:
                        var ellipseGeometry = new EllipseGeometry(
                            new System.Windows.Point(dxfCircle.Center.X, dxfCircle.Center.Y),
                            dxfCircle.Radius,
                            dxfCircle.Radius
                        );
                        var circlePath = new System.Windows.Shapes.Path
                        {
                            Data = ellipseGeometry,
                            Fill = Brushes.Transparent,
                            IsHitTestVisible = true
                        };
                        wpfShapes.Add(circlePath);
                        break;
                }
            }

            return wpfShapes;
        }

        /// <summary>
        /// Creates a WPF Path for a DXF Arc.
        /// </summary>
        private System.Windows.Shapes.Path? CreateArcPath(DxfArc dxfArc)
        {
            try
            {
                double startAngleRad = dxfArc.StartAngle * Math.PI / 180.0;
                double endAngleRad = dxfArc.EndAngle * Math.PI / 180.0;
                
                double arcStartX = dxfArc.Center.X + dxfArc.Radius * Math.Cos(startAngleRad);
                double arcStartY = dxfArc.Center.Y + dxfArc.Radius * Math.Sin(startAngleRad);
                var pathStartPoint = new System.Windows.Point(arcStartX, arcStartY);

                double arcEndX = dxfArc.Center.X + dxfArc.Radius * Math.Cos(endAngleRad);
                double arcEndY = dxfArc.Center.Y + dxfArc.Radius * Math.Sin(endAngleRad);
                var arcSegmentEndPoint = new System.Windows.Point(arcEndX, arcEndY);

                double sweepAngleDegrees = dxfArc.EndAngle - dxfArc.StartAngle;
                if (sweepAngleDegrees < 0) sweepAngleDegrees += 360;
                
                bool isLargeArc = sweepAngleDegrees > 180.0;
                SweepDirection sweepDirection = SweepDirection.Counterclockwise;

                ArcSegment arcSegment = new ArcSegment
                {
                    Point = arcSegmentEndPoint,
                    Size = new System.Windows.Size(dxfArc.Radius, dxfArc.Radius),
                    IsLargeArc = isLargeArc,
                    SweepDirection = sweepDirection,
                    RotationAngle = 0,
                    IsStroked = true
                };

                PathFigure pathFigure = new PathFigure
                {
                    StartPoint = pathStartPoint,
                    IsClosed = false
                };
                pathFigure.Segments.Add(arcSegment);

                PathGeometry pathGeometry = new PathGeometry();
                pathGeometry.Figures.Add(pathFigure);

                return new System.Windows.Shapes.Path
                {
                    Data = pathGeometry,
                    Fill = Brushes.Transparent,
                    IsHitTestVisible = true
                };
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Converts a DXF Line entity into a list of two System.Windows.Point objects.
        /// </summary>
        public List<System.Windows.Point> ConvertLineToPoints(DxfLine line)
        {
            var points = new List<System.Windows.Point>();
            if (line == null) return points;
            points.Add(new System.Windows.Point(line.P1.X, line.P1.Y));
            points.Add(new System.Windows.Point(line.P2.X, line.P2.Y));
            return points;
        }

        /// <summary>
        /// Converts a DXF Arc entity into a list of discretized System.Windows.Point objects.
        /// </summary>
        public List<System.Windows.Point> ConvertArcToPoints(DxfArc arc, double resolutionDegrees)
        {
            var points = new List<System.Windows.Point>();
            if (arc == null || resolutionDegrees <= 0) return points;

            double startAngle = arc.StartAngle;
            double endAngle = arc.EndAngle;
            double radius = arc.Radius;
            System.Windows.Point center = new System.Windows.Point(arc.Center.X, arc.Center.Y);

            // Normalize angles
            if (endAngle < startAngle) endAngle += 360;

            double currentAngle = startAngle;
            while (currentAngle <= endAngle)
            {
                double radAngle = currentAngle * Math.PI / 180.0;
                double x = center.X + radius * Math.Cos(radAngle);
                double y = center.Y + radius * Math.Sin(radAngle);
                points.Add(new System.Windows.Point(x, y));
                
                currentAngle += resolutionDegrees;
            }

            // Ensure end point is included
            if (Math.Abs(currentAngle - resolutionDegrees - endAngle) > 0.001)
            {
                double endRadAngle = endAngle * Math.PI / 180.0;
                double endX = center.X + radius * Math.Cos(endRadAngle);
                double endY = center.Y + radius * Math.Sin(endRadAngle);
                points.Add(new System.Windows.Point(endX, endY));
            }

            return points;
        }

        /// <summary>
        /// Converts a DXF Circle entity to a list of points representing its perimeter.
        /// </summary>
        public List<System.Windows.Point> ConvertCircleToPoints(DxfCircle circle, double resolutionDegrees)
        {
            List<System.Windows.Point> points = new List<System.Windows.Point>();
            if (circle == null || resolutionDegrees <= 0) return points;

            for (double angle = 0; angle < 360.0; angle += resolutionDegrees)
            {
                double radAngle = angle * Math.PI / 180.0;
                double x = circle.Center.X + circle.Radius * Math.Cos(radAngle);
                double y = circle.Center.Y + circle.Radius * Math.Sin(radAngle);
                points.Add(new System.Windows.Point(x, y));
            }

            return points;
        }

        /* // LightWeightPolyline processing removed as per subtask
        /// <summary>
        /// Converts a DXF LwPolyline entity into a list of discretized System.Windows.Point objects.
        /// </summary>
        public List<System.Windows.Point> ConvertLwPolylineToPoints(LightWeightPolyline polyline, double arcResolutionDegrees)
        {
            var points = new List<System.Windows.Point>();
            if (polyline == null || polyline.Vertices.Count == 0) return points;
            for (int i = 0; i < polyline.Vertices.Count; i++) {
                var currentVertexInfo = polyline.Vertices[i];
                System.Windows.Point currentDxfPoint = new System.Windows.Point(currentVertexInfo.Position.X, currentVertexInfo.Position.Y);
                points.Add(currentDxfPoint);
                if (Math.Abs(currentVertexInfo.Bulge) > 0.0001) {
                    if (!polyline.IsClosed && i == polyline.Vertices.Count - 1) continue;
                    // TODO: Implement LwPolyline bulge to Arc conversion for trajectory points.
                }
            }
            if (polyline.IsClosed && points.Count > 1 && System.Windows.Point.Subtract(points.First(), points.Last()).Length > 0.001) { // Requires System.Linq for .First() and .Last()
                 points.Add(points[0]);
            } else if (polyline.Vertices.Count == 1 && !points.Any()){ // Requires System.Linq for .Any()
                 points.Add(new System.Windows.Point(polyline.Vertices[0].Position.X, polyline.Vertices[0].Position.Y));
            }
            return points;
        }
        */
    }
}
