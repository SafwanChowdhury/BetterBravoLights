using System;
using System.Globalization;
using BravoLights.Common;

namespace BravoLights.UI
{
    /// <summary>
    /// A variable referenced by an expression, and its current value.
    /// </summary>
    public class VariableState : ViewModelBase
    {
        public VariableState()
        {
            ValueText = "No value received yet";
            IsError = true;
        }

        public string Name { get; set; }

        private string val;
        public string ValueText
        {
            get { return val; }
            private set
            {
                SetProperty(ref val, value);
            }
        }

        private bool isError;
        public bool IsError
        {
            get { return isError; }
            private set
            {
                SetProperty(ref isError, value);
            }
        }

        private object valueObject;
        public object Value
        {
            get { return valueObject; }
            set
            {
                SetProperty(ref valueObject, value);

                var exception = value as Exception;

                IsError = exception != null;

                if (exception != null)
                {
                    ValueText = exception.Message;
                }
                else if (value is double d)
                {
                    ValueText = d.ToString("0.######", CultureInfo.InvariantCulture);
                }
                else
                {
                    ValueText = Convert.ToString(value, CultureInfo.InvariantCulture);
                }
            }
        }
    }
}
