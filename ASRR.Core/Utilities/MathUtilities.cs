using System;

namespace ASRR.Core.Utilities
{
    public class MathUtilities
    {
        public static double DegreesToRadians(double degrees)
        {
            return degrees * Math.PI / 180;
        }

        public static double RadiansToDegrees(double radians)
        {
            return radians * 180 / Math.PI;
        }

        public static bool ApproximateEquals(double one, double two, double epsilon = 0.05)
        {
            return Math.Abs(one - two) < epsilon;
        }
    }
} 
