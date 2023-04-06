using Micros.Ops;
using Micros.Ops.Extensibility;
using Micros.PosCore.Checks;
using Micros.PosCore.Extensibility;
using Micros.PosCore.Extensibility.Ops;
using System;
using System.Threading;
using Newtonsoft.Json;
using Micros_Hello_World;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.IO;
using System.Diagnostics;

namespace MyExtensionApplication
{
    /// <summary>
    /// 
    public enum ScanTypes
    {
        NONE,
        PAY,
        REDEEM,
        EARN
    }

    public enum PaymentType
    {
        Wallet,
        Card,
        App,
        None
    }

    public class DiscountInfo
    {
        public string amount { get; set; }
        public string reference { get; set; }
        public string tender_type { get; set; }
    }

    public class ResponseObject
    {
        public bool success { get; set; }
        public string message { get; set; }
        public List<DiscountInfo> content { get; set; }
    }

    public class Application : OpsExtensibilityApplication
    {
        List<string> QRCodes = new List<string>();
        string HTTPResponseMessage = "";
        string Reference;
        string Endpoint;
        ScanTypes QR_CodeScanType = ScanTypes.NONE;
        bool RepeatScanOfQRCodes = false;
        static readonly string SimScriptTrxFile = @"C:\Micros\Simphony\TrxInfo.txt";
        static readonly string OfflineFilesLocation = @"C:\Micros\Simphony\WebServer\OfflineTransactions";
        static string VersionNumber = "0.13";
#if DEBUG
        static readonly string VersionName = VersionNumber+"QA";
        static readonly string POS_API_BASE_ENDPOINT = "http://pos-integration-dev.eu-de.mybluemix.net/pos/";
#else
        static readonly string VersionName = VersionNumber+"Prod";
        static readonly string POS_API_BASE_ENDPOINT = "http://pos-integration-prod.eu-de.mybluemix.net/pos/";
#endif
        private static readonly log4net.ILog log = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);
        /// Extension application constructor
        /// </summary>
        /// <param name="context">the execution context for the application</param>
        public Application(IExecutionContext context)
            : base(context)
        {
            // TODO: Add initialization code and hook up event handlers here
            //this.OpsTmedEvent += OnTmedEvent;
            Micros_Hello_World.Logger.Setup();
        }

        [ExtensibilityMethod]
        public void MyExtensionQRPay()
        {
            //Check Discord channel/docs for full flow.
            //Summary is:
            //Check for saved receipt, if not found (Not doing this anymore)
            //Validate order
            //Capture QR Code
            //Serialize order into JSON
            //Send order to backend
            //Apply discount and reference number based on server response.
            /*
            if (CheckForSavedReceiptInfo(OpsContext.Check.Guid) == false)
            {

            }
            else
            {
                OpsContext.ShowMessage("Saved Order Found!");
            }
            */
            log.Info("===========Logging new Transaction===========");
            log.InfoFormat("Check Number - {0}", OpsContext.CheckNumber);
            log.Info("Validating Order");
            if (!ValidateOrder())
            {
                log.Warn("Order incomplete/invalid");
                OpsContext.ShowMessage("Order incomplete/invalid");
                return;
            }

            log.Info("Performing QR Capture");
            if (!PerformQRCapture()) 
            {
                log.Warn("QR Capture Failed/Cancelled");
                return;
            }

            if(QR_CodeScanType == ScanTypes.NONE)
            {
                log.Warn("Invalid QR Code entered");
                OpsContext.ShowMessage("Invalid QR Code");
                return;
            }

            log.Info("Formatting Check Info");
            string JSONOrderInfo = SerializeOrderInfo();
            log.InfoFormat("Serialised order:");
            log.Info(JSONOrderInfo);
            log.Info("Sending check info");
            //Send request here.
            bool PostResult = false;
            while (PostResult != true)
            {
                PostResult = HTTPPostSyncrhonous(Endpoint, JSONOrderInfo);
                if (PostResult)
                {
                    log.Info("Network request successful, response:");
                    log.Info(this.HTTPResponseMessage);
                    /*
                    this.HTTPResponseMessage = @"{
                                                'success': true,
                                                'message': 'User successfully redeemed',
                                                'content': [
                                                       {
                                                        'amount': '50.00', 
                                                        'reference': 'R50 off ref123'
                                                       },
                                                       {
                                                        'amount': '25.00', 
                                                        'reference': 'R25 off ref456'
                                                       }
                                                  ]
                                            }";
                    */
                    //this.HTTPResponseMessage = @"{'success':true,'message':'User redeemed!.','content':[{'tender_type':'Card','amount':'50.0','reference':'CB42UFVWVM7CGIV1NEP8'}]}";
                    ProcessServerResponse(this.HTTPResponseMessage);
                }
                else
                {
                    log.Error("Network request failed");
                    log.Error(this.HTTPResponseMessage);
                    if (OpsContext.AskQuestion(string.Format("Request Failed - {0}.\n Yes to try again, No to cancel.", this.HTTPResponseMessage)))
                    {
                        log.Info("Request retrying");
                        continue;
                    }
                    else
                    {
                        log.Info("Cancelling retry");
                        //SaveQRCodeInfo(OpsContext.Check.Guid);
                        break;
                    }
                }
            }
            log.Info("===========End of Transaction===========");
            return;
        }

        void DecodeQRCodeInfo(ScanTypes Type)
        {
            this.QR_CodeScanType = Type;
            switch (Type)
            {
                case ScanTypes.PAY:
                    log.Info("PAY QR Code Detected");
                    this.Endpoint = "pay";
                    break;
                case ScanTypes.REDEEM:
                    log.Info("REDEEM QR Code Detected");
                    this.Endpoint = "redeem";
                    //Customer can scan multiple redemption vouchers.
                    this.RepeatScanOfQRCodes = false; //This was previously true but has been set to false per request. Modify this to true to revert
                    break;
                case ScanTypes.EARN:
                    log.Info("EARN QR Code Detected");
                    this.Endpoint = "earn";
                    break;
                default:
                    this.RepeatScanOfQRCodes = false;
                    return;
            }
        }

    bool CheckForCamera()
        {
            //System.Management.ManagementObject info = default(System.Management.ManagementObject);
            //System.Management.ManagementObjectSearcher search = default(System.Management.ManagementObjectSearcher);
            string deviceName;
            var search = new System.Management.ManagementObjectSearcher("SELECT * From Win32_PnPEntity");
            bool result = false;
            foreach (var Deviceinfo in search.Get())
            { //This tries to look for a camera device. Not the best way to do it. Other devices or drivers can trip this but it's not a big problem.
              //Incorrectly saying there is no camera when there is one would be worse.
                deviceName = Convert.ToString(Deviceinfo["Caption"]);
                if (deviceName.ToLower().Contains("cam"))
                {
                    result = true;
                }
            }
            return result;
        }

        string SerializeOrderInfo()
        {
            var CheckDetails = OpsContext.CheckDetail;
            var Guid = OpsContext.GetOpsContext().Check.Guid;
            var MyChequeObject = new ChequeInformation(this.QRCodes,
                                                       OpsContext.CheckNumber,
                                                       OpsContext.CheckTableNumber,
                                                       OpsContext.TransEmployeeID,
                                                       OpsContext.PropertyNumber,
                                                       OpsContext.CheckTotalDue,
                                                       Guid);
            foreach (var TopLevelItem in CheckDetails)
            {
                long MiObjNum = 0;
                long MajGrpObjNum = 0;
                long FamGrpObjNum = 0;
                var ComboSides = new List<MenuItem>();
                var Condiments = new List<MenuItem>();
                try
                {
                    OpsMenuItemDetail ItemID = (OpsMenuItemDetail)TopLevelItem;
                    var Valid = ItemID.MenuItemPriceKey.Valid;
                    MiObjNum = ItemID.MiObjNum;
                    MajGrpObjNum = ItemID.MajGrpObjNum;
                    FamGrpObjNum = ItemID.FamGrpObjNum;
                    foreach (var Sideitem in ItemID.Condiments)
                    {
                        try
                        {
                            var Condiment = new MenuItem(Sideitem.Name, Sideitem.MiObjNum, Sideitem.FamGrpObjNum, Sideitem.FamGrpObjNum, Sideitem.Total);
                            Condiments.Add(Condiment);
                        }
                        catch
                        {

                        }

                    }
                    foreach (var Sideitem in ItemID.ComboSides)
                    {
                        try
                        {
                            var ComboSide = new MenuItem(Sideitem.Name, Sideitem.MiObjNum, Sideitem.FamGrpObjNum, Sideitem.FamGrpObjNum, Sideitem.Total);
                            ComboSides.Add(ComboSide);
                        }
                        catch
                        {

                        }

                    }
                }
                catch
                {

                }
                var MainMenuItem = new MainMenuItem(TopLevelItem.Name, MiObjNum, MajGrpObjNum, FamGrpObjNum, TopLevelItem.Total, ComboSides);
                MainMenuItem.AddExtras(Condiments);

                //string TestJsonTwo = JsonConvert.SerializeObject(ItemID);
                MyChequeObject.AddMainMenuItem(MainMenuItem);
            }
            return JsonConvert.SerializeObject(MyChequeObject);
        }
        public bool MyExtensionQR()
        {
            var qrOperation = new QrScan();
            int Timeout = 30;//Enter value in seconds here. We pass value in milliseconds lower by multiplying by 1000.
            OpsContext.ShowMessage(string.Format("Launching QR Scanner. Scanner will exit after {0} seconds without a QR Code.",Timeout));
            bool result = qrOperation.StartCamera(Timeout*1000); // should be blocking
            if (result)
            {
                this.QRCodes.Add(qrOperation.GetQrCode());
                //OpsContext.ShowMessage(string.Format(this.QRCode));
            }
            else
            {
                //OpsContext.ShowMessage(string.Format("Scan Failed"));
            }
            return result;
        }
        bool HTTPPostSyncrhonous(string endpoint, string body)
        {
            var httpOperation = new HttpMessenger(POS_API_BASE_ENDPOINT);
            var Tuple = httpOperation.Http_Post(endpoint, body);
            var SuccessFlag = Tuple.Item1;
            this.HTTPResponseMessage = Tuple.Item2;
            return SuccessFlag;
        }

        void ProcessServerResponse(string data)
        {
            ResponseObject Response;
            try 
            {
                log.Info("Attempting to deserialise response");
                Response = JsonConvert.DeserializeObject<ResponseObject>(data);
            }
            catch(Exception e)
            {
                log.Error("Failed to deserialise response");
                log.Error(e.Message);
                OpsContext.ShowMessage(string.Format("Error: {0}\n Failed to deserialize: {1}", e.Message,data));
                return;
            }
            if(!Response.success)
            {
                log.Error("Server indicates request was not successful");
                OpsContext.ShowMessage(string.Format("Error: {0}", Response.message));
                return;
            }
            log.Info("Successful request");
            OpsContext.ShowMessage(string.Format("Success! - {0}", Response.message));
            if(Response.content == null)
            {
                log.Warn("No data to add to check");
                OpsContext.ShowMessage(string.Format("No data to add to check"));
                return;
            }
            //log.Info("Deleting old SIM script file");
            //File.Delete(SimScriptTrxFile);
            int Count = 0;
            foreach (var item in Response.content)
            {
                Count++;
                log.InfoFormat("Applying item {0}", Count);
                if (this.QR_CodeScanType == ScanTypes.PAY || this.QR_CodeScanType == ScanTypes.REDEEM)
                {
                    try
                    {
                        if(item.amount is null)
                        {
                            log.ErrorFormat("Amount is null on item - {0}",item);
                            throw new ArgumentException("Parameter cannot be null");
                        }
                        decimal Value = Convert.ToDecimal(item.amount);
                    }
                    catch
                    {
                        log.Error("Skipping item with null value");
                        //OpsContext.ShowMessage(string.Format("Failed to convert discount/payment value - {0}", item.amount));
                        continue;
                    }
                    if(this.QR_CodeScanType == ScanTypes.PAY)
                    {
                        log.Info("Pay QR Code method");
                        PaymentType PaymentMethod = DecodePaymentType(item.tender_type);
                        ApplyPayment(item.amount, PaymentMethod, item.reference);
                    }
                    else if(this.QR_CodeScanType == ScanTypes.REDEEM)
                    {
                        log.Info("Redemption QR Code method");
                        ApplyPayment(item.amount, PaymentType.App, item.reference);
                        //Changed this to payment instead of discount per request
                        //ApplyDiscount(item.amount, item.reference);
                    }
                }
                else if(this.QR_CodeScanType == ScanTypes.EARN)
                {
                    log.Info("Earn QR Code method");
                    AddCheckPrintLine(item.reference);
                }
            }
            /*
            log.Info("Checking that SIM communication file exists");
            if (File.Exists(SimScriptTrxFile))
            {
                log.Info("File found, executing SIM script");
                DoSimEnquire();
            }
            else
            {
                log.Warn("File not found. Should only occur if Earn QR code scanned.");
            }
            */
        }
        public bool PerformQRCapture()
        {
            this.QRCodes.Clear();
            do
            {
                log.Info("Checking for camera");
                if (CheckForCamera())
                {
                    log.Info("Camera found");
                    int CaptureMethod = PromptForCameraOrManualEntry();
                    if (CaptureMethod == 1)
                    {
                        log.Info("Using camera entry");
                        int QRCodeCount = QRCodes.Count;
                        if (!MyExtensionQR())
                        {
                            log.Info("Camera entry failed. Prompting for manual");
                            if (!QRManualEntry("Scan Failed. Enter Code Manually?", true))
                                return false;
                        }
                        else
                        {
                            log.Info("Camera entry successful");
                            OpsContext.ShowMessage(string.Format("Scan Successful! QR Code is:\n{0}", this.QRCodes[QRCodeCount]));
                        }
                    }
                    else if(CaptureMethod == 0)
                    {
                        log.Info("Using manual entry");
                        if (!QRManualEntry("", false))
                            return false;
                    }
                    else
                    {
                        return false;
                    }

                }
                else
                {
                    log.Warn("No Camera found");
                    if (!QRManualEntry("No camera detected. Enter Code Manually?", true))
                        return false;

                }
                try
                {
                    log.InfoFormat("QR Code {0} received", this.QRCodes[QRCodes.Count - 1]);
                    switch (char.ToUpper(this.QRCodes[QRCodes.Count - 1][0]))
                    {
                        case 'P':
                            DecodeQRCodeInfo(ScanTypes.PAY);
                            break;
                        case 'R':
                            DecodeQRCodeInfo(ScanTypes.REDEEM);
                            break;
                        case 'E':
                            DecodeQRCodeInfo(ScanTypes.EARN);
                            break;
                        default:
                            DecodeQRCodeInfo(ScanTypes.NONE);
                            break;
                    }
                }
                catch
                {
                    DecodeQRCodeInfo(ScanTypes.NONE);
                }
                
            } while (this.RepeatScanOfQRCodes && OpsContext.AskQuestion("Extra Vouchers To Apply?") == true);
            log.Info("Done scanning QR Codes");
            return true;
        }
        public int PromptForCameraOrManualEntry()
        {
            //Camera selection should return true.
            var InputMethodsList = new List<OpsSelectionEntry>
            {
                new OpsSelectionEntry(0, "Manual"),
                new OpsSelectionEntry(1, "Camera")
            };
            var result = OpsContext.SelectionRequest("Input - " + VersionName,"Please choose a method", InputMethodsList);
            if (result != null && result >= 0)
                return (int)result;
            return -1;
        }
        public bool QRManualEntry(string message, bool InitialPrompt)
        {
            if(InitialPrompt == false)
            {
                this.QRCodes.Add(OpsContext.RequestAlphaEntry("Enter Code", "Manual Entry"));
                return true;
            }
            else
            {
                if (OpsContext.AskQuestion(message))
                {
                    try 
                    {
                        string Response = OpsContext.RequestAlphaEntry("Enter Code", "Manual Entry");
                        if (Response.Length > 0)
                        {
                            this.QRCodes.Add(Response);
                            return true;
                        }
                        else
                        {
                            return false;
                        }
                        
                    }
                    catch
                    {
                        return false;
                    }
                }
            }

            return false;
        }
        public void AddCheckPrintLine(string DiscountReference)
        {
            log.InfoFormat("Adding reference {0} to check",DiscountReference);
            if (DiscountReference == null || DiscountReference.Length == 0)
                return;
            try
            {
                //Remove all existing ExtensibilityDetail , the system somehow add duplicates
                //OpsContext.CheckContext.Check.ExtensibilityDetail.RemoveAll(this.ApplicationName);
                ExtensibilityDataInfo ExDataInfo = new ExtensibilityDataInfo(DiscountReference, string.Empty, string.Empty);
                OpsContext.CheckContext.Check.AddExtensibilityData(ExDataInfo);
            }
            catch (Exception ex)
            {
                OpsContext.ShowException(ex, "error adding ExtensibilityData");
            }
        }
        public bool ValidateOrder()
        {
            bool result = true;

            try
            {
                var CheckDetails = OpsContext.CheckDetail;
                //Check order isn't empty
                if(CheckDetails.Count == 0)
                {
                    return false;
                }
                foreach (var TopLevelItem in CheckDetails)
                {
                    try
                    {
                        var DetailLink = TopLevelItem.DetailLink;
                        var CondimentList = OpsContext.GetCondimentGroupStatus(DetailLink);
                        //Check that all condiment groups are satisfied
                        if(CondimentList == null)
                        {
                            continue;
                        }
                        foreach (var CondimentGroup in CondimentList)
                        {
                            if (!CondimentGroup.Satisfied)
                            {
                                result = false;
                            }
                        }
                    }
                    catch
                    {

                    }
                    
                }
            }
            catch (Exception ex)
            {
                OpsContext.ShowException(ex, "Failed to validate order");
                result = false;
            }
            
            return result;
        }
        public void ApplyDiscount(string Value, string Reference)
        {
            long TenderId = 9003;
            WriteTrxInfoToFile(false, TenderId, Value, Reference);
        }

        public void ApplyPayment(string Value, PaymentType Method, string Reference)
        {
            log.Info("Applying payment");
            log.InfoFormat("Amount = {0}", Value);
            log.InfoFormat("Reference = {0}", Reference);
            long TenderID;
            if (Method == PaymentType.Card)
                TenderID = 1000;
            else if (Method == PaymentType.Wallet)
                TenderID = 1001;
            else if (Method == PaymentType.App)
                TenderID = 1002;
            else
            {
                log.Error("No payment method specified");
                OpsContext.ShowMessage(string.Format("No payment type specified"));
                return;
            }
            /*
            log.Info("Writing trx to file");
            WriteTrxInfoToFile(true, TenderID, Value, Reference);
            */
            log.Info("Executing payment command");
            CommandResult result;
            int count = 0;
            this.OpsAsciiDataEvent += AddCustomRefHandler;
            this.Reference = Reference;
            do
            {
                count++;
                OpsCommand AddPayment = new OpsCommand(OpsCommandType.Payment)
                {
                    Number = TenderID,
                    Arguments = "Cash:Cash",
                    Text = Value,
                };
                result = OpsContext.ProcessCommand(AddPayment);
                if (!result.IsSuccess)
                {
                    log.ErrorFormat("Failed to add payment on try {0}, reason - {1}", count, result.Id.ToString());
                }
            } while (!result.IsSuccess && OpsContext.AskQuestion(string.Format("Failed to add payment - {0}\nRetry?", result.Id.ToString())) == true);
            if (result.IsSuccess)
            {
                log.Info("Payment added successfully");
            }
            else
            {
                log.Error("Continuing without adding payment");
            }
            this.OpsAsciiDataEvent -= AddCustomRefHandler;
        }

        void WriteTrxInfoToFile(bool PaymentTrueDiscountFalse, long TenderID, string TrxAmount, string Reference)
        {
            string TrxInfoString;
            if (PaymentTrueDiscountFalse)
            {
                TrxInfoString = "Pay";
            }
            else
            {
                TrxInfoString = "Discount";
            }
            if (TrxAmount.Contains("."))
            {
                decimal Value = Convert.ToDecimal(TrxAmount);
                Value *= 100;
                int NoDecimalValue = (int)Value;
                TrxAmount = NoDecimalValue.ToString();
            }
            TrxInfoString += "," + TenderID + "," + TrxAmount + "," + Reference + System.Environment.NewLine;
            File.AppendAllText(SimScriptTrxFile, TrxInfoString);
        }

        void DoSimEnquire()
        {
            OpsCommand SimEnquireAddPayment = new OpsCommand(OpsCommandType.SimInquire)
            {
                Arguments = "DoshexExtension010:1"
            };
            var result = OpsContext.ProcessCommand(SimEnquireAddPayment);
            if (!result.IsSuccess)
            {
                OpsContext.ShowMessage(string.Format("Failed to add payment - {0}", result.Id.ToString()));
            }
        }

        PaymentType DecodePaymentType(string Method)
        {
            if (Method == null)
            {
                OpsContext.ShowMessage(string.Format("No Payment Method Specified"));
                return PaymentType.None;
            }

            if (Method.Equals("Wallet", StringComparison.CurrentCultureIgnoreCase))
            {
                return PaymentType.Wallet;
            }
            else if(Method.Equals("Card", StringComparison.CurrentCultureIgnoreCase))
            {
                return PaymentType.Card;
            }
            OpsContext.ShowMessage(string.Format("Invalid payment method"));
            return PaymentType.None;
        }

        public void SaveQRCodeInfo(string guid)
        {
            try
            {
                // Determine whether the directory exists.
                if (!Directory.Exists(OfflineFilesLocation))
                {
                    DirectoryInfo di = Directory.CreateDirectory(OfflineFilesLocation);
                }
                TextWriter tw = new StreamWriter(OfflineFilesLocation+"\\"+guid+".txt");

                foreach (string s in QRCodes)
                    tw.WriteLine(s);

                tw.Close();
            }
            catch (Exception e)
            {
                Console.WriteLine("Saving the QR codes failed: {0}", e.ToString());
            }
            
        }

        public bool CheckForSavedReceiptInfo(string guid)
        {
            this.QRCodes.Clear();
            string filename = OfflineFilesLocation + "\\" + guid + ".txt";
            try
            {
                if (File.Exists(filename))
                {
                    string line;
                    // Read the file and display it line by line.  
                    StreamReader file_reader = new StreamReader(filename);
                    while ((line = file_reader.ReadLine()) != null)
                    {
                        QRCodes.Add(line);
                    }
                    file_reader.Close();
                    File.Delete(filename);
                    return true;
                }
            }
            catch(Exception e)
            {
                OpsContext.ShowMessage(string.Format("Failed to check for existing receipt - {0}", e.Message));
            }
            return false;
        }
        [ExtensibilityMethod]
        public void DeleteSavedReceiptInfo()
        {
            string guid = OpsContext.Check.Guid;
            string filename = OfflineFilesLocation + "\\" + guid + ".txt";
            try
            {
                File.Delete(filename);
            }
            catch
            {

            }
        }

        private void AddCustomRef()
        {
            //We enter this event when the cashier presses a key while on the "Enter Reference" Screen when a payment occurs
            //Execute backspace to remove first key entered.
            OpsCommand BackSpace = new OpsCommand(OpsCommandType.Backspace);
            var result = OpsContext.ProcessCommand(BackSpace);
            if (!result.IsSuccess)
            {
                log.Error(string.Format("Failed to add execute backspace, reason - {0}", result.Id.ToString()));
            }
            //Enter Macro for ascii data
            var AddReference = new OpsCommand(OpsCommandType.AsciiData)
            {
                Text = this.Reference
            };
            result = OpsContext.ProcessCommand(AddReference);
            if (!result.IsSuccess)
            {
                log.Error(string.Format("Failed to add custom reference, reason - {0}", result.Id.ToString()));
            }
            //Execute enter key to automatically continue
            var EnterKey = new OpsCommand(OpsCommandType.EnterKey);
            result = OpsContext.ProcessCommand(EnterKey);
            if (!result.IsSuccess)
            {
                log.Error(string.Format("Failed to add execute enter key, reason - {0}", result.Id.ToString()));
            }
        }

        private EventProcessingInstruction AddCustomRefHandler(object sender, OpsCommandEventArgs args)
        {
            try
            {
                AddCustomRef();
                return (EventProcessingInstruction)0;
            }
            catch (Exception e)
            {
                log.Error("Error in add reference event handler", e);
                return (EventProcessingInstruction)1;
            }

        }

    }

    /// <summary>
    ///  Implements interface used by Simphony POS Client to create an instance of the extension application
    /// </summary>
    public class ApplicationFactory : IExtensibilityAssemblyFactory
    {
        public ExtensibilityAssemblyBase Create(IExecutionContext context)
        {
            return new Application(context);
        }

        public void Destroy(ExtensibilityAssemblyBase app)
        {
            app.Destroy();
        }
    }
}
