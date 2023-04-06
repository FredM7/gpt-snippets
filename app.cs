using Micros.Ops;
using Micros.Ops.Extensibility;
using Micros.PosCore.Checks;
using Micros.PosCore.Extensibility;
using Micros.PosCore.Extensibility.Ops;
using System;
using Newtonsoft.Json;
using System.Collections.Generic;
using System.IO;
using DoshxExtension.models;
using DoshxExtension.enums;
using System.Threading.Tasks;
using log4net;

namespace MyExtensionApplication
{
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

  public class Application : OpsExtensibilityApplication
  {
    static readonly string VersionNumber = "0.19";

#if DEBUG
        static readonly string VersionName = VersionNumber + "QA";
        static readonly string POS_API_BASE_ENDPOINT = "https://pos-integration-dev-lelonmpsuq-ew.a.run.app/pos/";
#else
    readonly string VersionName = VersionNumber + "Prod";
    readonly string POS_API_BASE_ENDPOINT = "https://pos-integration-prod-lelonmpsuq-ew.a.run.app/pos/";
#endif

    string Reference;
    string Endpoint;
    bool RepeatScanOfQRCodes = false;
    List<string> QRCodes = new List<string>();
    ScanType QR_CodeScanType = ScanType.NONE;

    //readonly string SimScriptTrxFile = @"C:\Micros\Simphony\TrxInfo.txt";
    readonly string OfflineFilesLocation = @"C:\Micros\Simphony\WebServer\OfflineTransactions";

    private readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

    public Application(IExecutionContext context)
        : base(context)
    {
      DoshxExtension.Logger.Setup();
    }

    [ExtensibilityMethod] //For the EMC
    public async void MyExtensionQRPay() //Function pointer in EMC. If you change this, the Arguments in EMC UI/Button must also change.
    {
      log.Info("======= Logging new Transaction =======");
      log.InfoFormat("Check Number - {0}", OpsContext.CheckNumber);

      if (!ValidateOrder())
      {
        log.Warn("Order incomplete/invalid");
        OpsContext.ShowMessage("Order incomplete/invalid");
        return;
      }

      if (!PerformQRCapture())
      {
        log.Warn("QR Capture Failed/Cancelled");
        return;
      }

      if (QR_CodeScanType == ScanType.NONE)
      {
        log.Warn("Invalid QR Code entered");
        OpsContext.ShowMessage("Invalid QR Code");
        return;
      }

      await MakeRequest();

      return;
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

    async Task<ResponseObject> MakeRequest()
    {
      string JSONOrderInfo = SerializeOrderInfo();
      log.InfoFormat("Serialised order:");
      log.Info(JSONOrderInfo);
      log.Info("Sending check info");

      ResponseObject postResult = await HTTPPostAsync(Endpoint, JSONOrderInfo);
      if (postResult.success)
      {
        log.Info("Network request successful, response:");
        log.Info(postResult.message);

        ProcessServerResponse(postResult.message);
      }
      else
      {
        log.Error("Network request failed");
        log.Error(postResult.message);
        if (OpsContext.AskQuestion($"Request Failed - {postResult.message}.\n Yes to try again, No to cancel."))
        {
          log.Info("Yes pressed. Request is retrying...");
          postResult = await MakeRequest();
        }
        else
        {
          log.Info("No pressed. Retry has been cancelled.");
          //SaveQRCodeInfo(OpsContext.Check.Guid);
        }
      }

      log.Info("===========End of Transaction===========");
      return postResult;
    }

    void DecodeQRCodeInfo(ScanType Type)
    {
      this.QR_CodeScanType = Type;
      switch (Type)
      {
        case ScanType.PAY:
          log.Info("PAY QR Code Detected");
          this.Endpoint = "pay";
          break;
        case ScanType.REDEEM:
          log.Info("REDEEM QR Code Detected");
          this.Endpoint = "redeem";
          //Customer can scan multiple redemption vouchers.
          this.RepeatScanOfQRCodes = false; //This was previously true but has been set to false per request. Modify this to true to revert
          break;
        case ScanType.EARN:
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

      log.Info("Formatting Check Info");
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
      OpsContext.ShowMessage(string.Format("Launching QR Scanner. Scanner will exit after {0} seconds without a QR Code.", Timeout));
      bool result = qrOperation.StartCamera(Timeout * 1000); // should be blocking
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

    async Task<ResponseObject> HTTPPostAsync(string endpoint, string body)
    {
      log.Info("Attempting to call Http_PostAsync()");
      ResponseObject ro = new ResponseObject();
      try
      {
        var httpOperation = new HttpMessenger(POS_API_BASE_ENDPOINT);
        var Tuple = await httpOperation.Http_PostAsync(endpoint, body);

        ro.success = Tuple.Item1;
        ro.message = Tuple.Item2;
      }
      catch (Exception ex)
      {
        String message = $"Error were encoutered while calling Http_Post():\n{ex.Message}";
        log.Error(message);
        ro.success = false;
        ro.message = message;
      }
      return ro;
    }

    ResponseObject ProcessServerResponse(string data)
    {
      ResponseObject response = new ResponseObject();

      try
      {
        log.Info("Attempting to deserialise response.");
        response = JsonConvert.DeserializeObject<ResponseObject>(data);
      }
      catch (Exception e)
      {
        log.Error($"Failed to deserialise the response.\n{e.Message}");
        OpsContext.ShowMessage($"Error: {e.Message}\nFailed to deserialize: {data}");
        return response;
      }

      if (!response.success)
      {
        log.Error("Server indicated that the request was NOT successful.");
        log.Error(response.message ?? "");
        OpsContext.ShowMessage($"Error: {response.message ?? ""}");
        return response;
      }
      else
      {
        log.Info("Successful request");
        OpsContext.ShowMessage($"Success! - {response.message}");
      }

      if (response.content == null)
      {
        log.Warn("No data to add to check.");
        OpsContext.ShowMessage("No data to add to check.");
        return response;
      }

      //log.Info("Deleting old SIM script file");
      //File.Delete(SimScriptTrxFile);
      int Count = 0;
      foreach (var item in response.content)
      {
        Count++;
        log.Info($"Applying item {Count}");

        if (QR_CodeScanType == ScanType.PAY || QR_CodeScanType == ScanType.REDEEM)
        {
          if (item.amount is null)
          {
            log.Error($"Amount is null. Skipping item - {item}");
            //throw new ArgumentException("Parameter cannot be null");
            continue;
          }

          if (QR_CodeScanType == ScanType.PAY)
          {
            log.Info("Pay QR Code method.");
            PaymentType PaymentMethod = DecodePaymentType(item.tender_type);
            ApplyPayment(item.amount, PaymentMethod, item.reference);
          }
          else if (QR_CodeScanType == ScanType.REDEEM)
          {
            log.Info("Redeem QR Code method.");
            ApplyPayment(item.amount, PaymentType.App, item.reference);
            //Changed this to payment instead of discount per request
            //ApplyDiscount(item.amount, item.reference);
          }
        }
        else if (QR_CodeScanType == ScanType.EARN)
        {
          log.Info("Earn QR Code method");
          AddCheckPrintLine(item.reference);
        }
      }

      return response;
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
      log.Info("Performing QR Capture");

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
          else if (CaptureMethod == 0)
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
              DecodeQRCodeInfo(ScanType.PAY);
              break;
            case 'R':
              DecodeQRCodeInfo(ScanType.REDEEM);
              break;
            case 'E':
              DecodeQRCodeInfo(ScanType.EARN);
              break;
            default:
              DecodeQRCodeInfo(ScanType.NONE);
              break;
          }
        }
        catch
        {
          DecodeQRCodeInfo(ScanType.NONE);
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

      var result = OpsContext.SelectionRequest("Input - " + VersionName, "Please choose a method", InputMethodsList);

      if (result != null && result >= 0)
        return (int)result;
      return -1;
    }

    public bool QRManualEntry(string message, bool InitialPrompt)
    {
      if (InitialPrompt == false)
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
      log.InfoFormat("Adding reference {0} to check", DiscountReference);
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
        log.Info("Validating Order");

        var checkDetails = OpsContext.CheckDetail;
        //Check order isn't empty
        if (checkDetails.Count == 0)
        {
          return false;
        }

        bool continueLooping = true;
        foreach (var topLevelItem in checkDetails)
        {
          try
          {
            if (!continueLooping)
            {
              break;
            }

            var condimentList = OpsContext.GetCondimentGroupStatus(topLevelItem.DetailLink);
            //Check that all condiment groups are satisfied
            if (condimentList == null)
            {
              //It's OK to be empty.
              continue;
            }

            foreach (var CondimentGroup in condimentList)
            {
              if (!CondimentGroup.Satisfied)
              {
                result = false;
                //Why continue looping? Let's break here.
                continueLooping = false;
                break;
              }
            }
          }
          catch (Exception e)
          {
            log.Error($"Error while looping inside ValidateOrder(). Error: {e.Message}", e);
            //Not necessary to return false tho. This might change if we come to doubt it.
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

    public void ApplyPayment(string Value, PaymentType Method, string Ref)
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

      log.Info("Executing payment command");
      CommandResult result;
      int count = 0;
      OpsAsciiDataEvent += AddCustomRefHandler;
      Reference = Ref;
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
          log.Error($"Failed to add payment on try {count}, reason - {result.Id.ToString()}");
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

      OpsAsciiDataEvent -= AddCustomRefHandler;
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
      else if (Method.Equals("Card", StringComparison.CurrentCultureIgnoreCase))
      {
        return PaymentType.Card;
      }
      OpsContext.ShowMessage(string.Format("Invalid payment method"));
      return PaymentType.None;
    }

    private EventProcessingInstruction AddCustomRefHandler(object sender, OpsCommandEventArgs args)
    {
      // First check if the Reference property is not null or empty before executing AddCustomRef.
      // If the property is null or empty, it simply returns without executing AddCustomRef.
      // This avoids unnecessary CPU usage for handling events when it's not necessary.
      if (!string.IsNullOrEmpty(Reference))
      {
        try
        {
          AddCustomRef();
          return 0; // (EventProcessingInstruction)0
        }
        catch (Exception e)
        {
          log.Error("Error in add reference event handler", e);
          return (EventProcessingInstruction)1;
        }
      }
      return 0; // (EventProcessingInstruction)0
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
        Text = Reference
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
  }
}



//public void ApplyDiscount(string Value, string Reference)
//{
//  long TenderId = 9003;
//  WriteTrxInfoToFile(false, TenderId, Value, Reference);
//}

//void DoSimEnquire()
//{
//  OpsCommand SimEnquireAddPayment = new OpsCommand(OpsCommandType.SimInquire)
//  {
//    Arguments = "DoshexExtension010:1"
//  };
//  var result = OpsContext.ProcessCommand(SimEnquireAddPayment);
//  if (!result.IsSuccess)
//  {
//    OpsContext.ShowMessage(string.Format("Failed to add payment - {0}", result.Id.ToString()));
//  }
//}

//public void SaveQRCodeInfo(string guid)
//{
//  try
//  {
//    // Determine whether the directory exists.
//    if (!Directory.Exists(OfflineFilesLocation))
//    {
//      DirectoryInfo di = Directory.CreateDirectory(OfflineFilesLocation);
//    }
//    TextWriter tw = new StreamWriter(OfflineFilesLocation + "\\" + guid + ".txt");

//    foreach (string s in QRCodes)
//      tw.WriteLine(s);

//    tw.Close();
//  }
//  catch (Exception e)
//  {
//    Console.WriteLine("Saving the QR codes failed: {0}", e.ToString());
//  }
//}

//public bool CheckForSavedReceiptInfo(string guid)
//{
//  this.QRCodes.Clear();
//  string filename = OfflineFilesLocation + "\\" + guid + ".txt";
//  try
//  {
//    if (File.Exists(filename))
//    {
//      string line;
//      // Read the file and display it line by line.  
//      StreamReader file_reader = new StreamReader(filename);
//      while ((line = file_reader.ReadLine()) != null)
//      {
//        QRCodes.Add(line);
//      }
//      file_reader.Close();
//      File.Delete(filename);
//      return true;
//    }
//  }
//  catch (Exception e)
//  {
//    OpsContext.ShowMessage(string.Format("Failed to check for existing receipt - {0}", e.Message));
//  }
//  return false;
//}
