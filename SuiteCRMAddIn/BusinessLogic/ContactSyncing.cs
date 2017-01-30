﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using System.Runtime.InteropServices;
using Newtonsoft.Json;
using SuiteCRMClient;
using SuiteCRMClient.RESTObjects;
using SuiteCRMClient.Logging;
using Outlook = Microsoft.Office.Interop.Outlook;

namespace SuiteCRMAddIn.BusinessLogic
{
    public class ContactSyncing: Syncing<Outlook.ContactItem>
    {
        public ContactSyncing(SyncContext context)
            : base(context)
        {
        }

        public void StartSync()
        {
            try
            {
                Log.Info("ContactSync thread starting");
                Outlook.NameSpace oNS = this.Application.GetNamespace("mapi");
                Outlook.MAPIFolder folder = GetDefaultFolder();
                Outlook.Items items = folder.Items;

                items.ItemAdd -= Items_ItemAdd;
                items.ItemChange -= Items_ItemChange;
                items.ItemRemove -= Items_ItemRemove;
                items.ItemAdd += Items_ItemAdd;
                items.ItemChange += Items_ItemChange;
                items.ItemRemove += Items_ItemRemove;

                GetOutlookItems(folder);
                SyncFolder(folder);
            }
            catch (Exception ex)
            {
                Log.Error("ThisAddIn.StartContactSync", ex);
            }
            finally
            {
                Log.Info("ContactSync thread completed");
            }
        }

        private void SyncFolder(Outlook.MAPIFolder contactFolder)
        {
            Log.Warn("ThisAddIn.SyncContacts");
            try
            {
                if (!HasAccess("Contacts", "export"))
                {
                    Log.Warn("CRM server denied access to export Contacts");
                    return;
                }

                var untouched = new HashSet<SyncState<Outlook.ContactItem>>(ItemsSyncState);
                int iOffset = 0;
                while (true)
                {
                    eGetEntryListResult _result2 = clsSuiteCRMHelper.GetEntryList("Contacts",
                                    "contacts.assigned_user_id = '" + clsSuiteCRMHelper.GetUserId() + "'",
                                    0, "date_entered DESC", iOffset, false, clsSuiteCRMHelper.GetSugarFields("Contacts"));
                    var nextOffset = _result2.next_offset;
                    if (iOffset == nextOffset)
                        break;

                    foreach (var oResult in _result2.entry_list)
                    {
                        try
                        {
                            var state = UpdateFromCrm(contactFolder, oResult);
                            if (state != null) untouched.Remove(state);
                        }
                        catch (Exception ex)
                        {
                            Log.Error("ThisAddIn.SyncContacts", ex);
                        }
                    }

                    iOffset = nextOffset;
                    if (iOffset == 0)
                        break;
                }
                try
                {
                    var lItemToBeDeletedO = untouched.Where(a => a.OutlookItem.Sensitivity == Outlook.OlSensitivity.olNormal && !string.IsNullOrWhiteSpace(a.OModifiedDate.ToString()));
                    foreach (var oItem in lItemToBeDeletedO)
                    {
                        oItem.OutlookItem.Delete();
                        ItemsSyncState.Remove(oItem);
                    }

                    var lItemToBeAddedToS = untouched.Where(a => a.OutlookItem.Sensitivity == Outlook.OlSensitivity.olNormal && string.IsNullOrWhiteSpace(a.OModifiedDate.ToString()));
                    foreach (var oItem in lItemToBeAddedToS)
                    {
                        AddToCrm(oItem.OutlookItem);
                    }
                }
                catch (Exception ex)
                {
                    Log.Error("ThisAddIn.SyncContacts", ex);
                }
            }
            catch (Exception ex)
            {
                Log.Error("ThisAddIn.SyncContacts", ex);
            }
        }

        private SyncState<Outlook.ContactItem> UpdateFromCrm(Outlook.MAPIFolder contactFolder, eEntryValue oResult)
        {
            dynamic dResult = JsonConvert.DeserializeObject(oResult.name_value_object.ToString());

            var oItem = ItemsSyncState.FirstOrDefault(a => a.CrmEntryId == dResult.id.value.ToString());
            if (oItem == null)
            {
                if (dResult.sync_contact.value.ToString() != "True")
                {
                    Log.Warn("not sync!");
                    return null;
                }

                Log.Warn("    default sync");
                Outlook.ContactItem cItem = contactFolder.Items.Add(Outlook.OlItemType.olContactItem);
                cItem.FirstName = dResult.first_name.value.ToString();
                cItem.LastName = dResult.last_name.value.ToString();
                cItem.Email1Address = dResult.email1.value.ToString();
                cItem.BusinessTelephoneNumber = dResult.phone_work.value.ToString();
                cItem.HomeTelephoneNumber = dResult.phone_home.value.ToString();
                cItem.MobileTelephoneNumber = dResult.phone_mobile.value.ToString();
                cItem.JobTitle = dResult.title.value.ToString();
                cItem.Department = dResult.department.value.ToString();
                cItem.BusinessAddressCity = dResult.primary_address_city.value.ToString();
                cItem.BusinessAddressCountry = dResult.primary_address_country.value.ToString();
                cItem.BusinessAddressPostalCode = dResult.primary_address_postalcode.value.ToString();
                cItem.BusinessAddressState = dResult.primary_address_state.value.ToString();
                cItem.BusinessAddressStreet = dResult.primary_address_street.value.ToString();
                cItem.Body = dResult.description.value.ToString();
                if (dResult.account_name != null)
                {
                    cItem.Account = dResult.account_name.value.ToString();
                    cItem.CompanyName = dResult.account_name.value.ToString();
                }
                cItem.BusinessFaxNumber = dResult.phone_fax.value.ToString();
                cItem.Title = dResult.salutation.value.ToString();

                Outlook.UserProperty oProp = cItem.UserProperties.Add("SOModifiedDate", Outlook.OlUserPropertyType.olText);
                oProp.Value = dResult.date_modified.value.ToString();
                Outlook.UserProperty oProp2 = cItem.UserProperties.Add("SEntryID", Outlook.OlUserPropertyType.olText);
                oProp2.Value = dResult.id.value.ToString();
                var newState = new ContactSyncState
                {
                    OutlookItem = cItem,
                    OModifiedDate = DateTime.ParseExact(dResult.date_modified.value.ToString(), "yyyy-MM-dd HH:mm:ss", null),
                    CrmEntryId = dResult.id.value.ToString(),
                };
                ItemsSyncState.Add(newState);
                Log.Warn(cItem.FullName + "     is saving with " + cItem.Sensitivity.ToString());
                cItem.Save();
                return newState;
            }
            else
            {
                Outlook.ContactItem cItem = oItem.OutlookItem;
                Outlook.UserProperty oProp = cItem.UserProperties["SOModifiedDate"];

                if (oProp.Value != dResult.date_modified.value.ToString())
                {
                    cItem.FirstName = dResult.first_name.value.ToString();
                    cItem.LastName = dResult.last_name.value.ToString();
                    cItem.Email1Address = dResult.email1.value.ToString();
                    cItem.BusinessTelephoneNumber = dResult.phone_work.value.ToString();
                    cItem.HomeTelephoneNumber = dResult.phone_home.value.ToString();
                    cItem.MobileTelephoneNumber = dResult.phone_mobile.value.ToString();
                    cItem.JobTitle = dResult.title.value.ToString();
                    cItem.Department = dResult.department.value.ToString();
                    cItem.BusinessAddressCity = dResult.primary_address_city.value.ToString();
                    cItem.BusinessAddressCountry = dResult.primary_address_country.value.ToString();
                    cItem.BusinessAddressPostalCode = dResult.primary_address_postalcode.value.ToString();

                    if (dResult.primary_address_street.value != null)
                        cItem.BusinessAddressStreet = dResult.primary_address_street.value.ToString();
                    cItem.Body = dResult.description.value.ToString();
                    cItem.Account = cItem.CompanyName = "";
                    if (dResult.account_name != null && dResult.account_name.value != null)
                    {
                        cItem.Account = dResult.account_name.value.ToString();
                        cItem.CompanyName = dResult.account_name.value.ToString();
                    }

                    cItem.BusinessFaxNumber = dResult.phone_fax.value.ToString();
                    cItem.Title = dResult.salutation.value.ToString();
                    if (oProp == null)
                        oProp = cItem.UserProperties.Add("SOModifiedDate", Outlook.OlUserPropertyType.olText);
                    oProp.Value = dResult.date_modified.value.ToString();
                    Outlook.UserProperty oProp2 = cItem.UserProperties["SEntryID"];
                    if (oProp2 == null)
                        oProp2 = cItem.UserProperties.Add("SEntryID", Outlook.OlUserPropertyType.olText);
                    oProp2.Value = dResult.id.value.ToString();
                    Log.Warn("    save not default");
                    Log.Warn(cItem.FullName + "     is saving with" + cItem.Sensitivity.ToString());
                    cItem.Save();
                }
                Log.Warn((string) (cItem.FullName + " dResult.date_modified= " + dResult.date_modified.ToString()));
                oItem.OModifiedDate = DateTime.ParseExact(dResult.date_modified.value.ToString(), "yyyy-MM-dd HH:mm:ss", null);
                return oItem;
            }
        }

        private void GetOutlookItems(Outlook.MAPIFolder taskFolder)
        {
            try
            {
                if (ItemsSyncState == null)
                {
                    ItemsSyncState = new List<SyncState<Outlook.ContactItem>>();
                    Outlook.Items items = taskFolder.Items.Restrict("[MessageClass] = 'IPM.Contact'");
                    foreach (Outlook.ContactItem oItem in items)
                    {
                        Outlook.UserProperty oProp = oItem.UserProperties["SOModifiedDate"];
                        if (oProp != null)
                        {
                            Outlook.UserProperty oProp2 = oItem.UserProperties["SEntryID"];
                            DateTime modDateTime = DateTime.UtcNow;
                            if (!DateTime.TryParseExact(oProp.Value.ToString(), "yyyy-MM-dd HH:mm:ss", null, DateTimeStyles.None, out modDateTime))
                            {
                                DateTime.TryParse(oProp.Value.ToString(), out modDateTime);
                            }
                            ItemsSyncState.Add(new ContactSyncState
                            {
                                OutlookItem = oItem,
                                OModifiedDate = modDateTime,
                                CrmEntryId = oProp2.Value.ToString()
                            });
                        }
                        else
                        {
                            ItemsSyncState.Add(new ContactSyncState
                            {
                                OutlookItem = oItem
                            });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error("ThisAddIn.GetOutlookCItems", ex);
            }
        }

        void Items_ItemChange(object Item)
        {
            Log.Warn("ItemChange");

            try
            {
                var oItem = Item as Outlook.ContactItem;

                Log.Warn(oItem.FullName + " Sensitivity= " + oItem.Sensitivity);
                string entryId = oItem.EntryID;
                Log.Warn("oItem.EntryID: " + entryId);
                var contact = ItemsSyncState.FirstOrDefault(a => a.OutlookItem.EntryID == entryId);
                Log.Warn("EntryID=  " + oItem.EntryID);
                if (contact != default(ContactSyncState))
                {
                    if ((int)Math.Abs((DateTime.UtcNow - contact.OModifiedDate).TotalSeconds) > 5)
                    {
                        contact.IsUpdate = 0;
                    }

                    Log.Warn("Before UtcNow - contact.OModifiedDate= " +
                                               (int)(DateTime.UtcNow - contact.OModifiedDate).TotalSeconds);
                    Log.Warn("IsUpdate before time check: " + contact.IsUpdate.ToString());
                    if ((int)Math.Abs((DateTime.UtcNow - contact.OModifiedDate).TotalSeconds) > 2 && contact.IsUpdate == 0)
                    {
                        contact.OModifiedDate = DateTime.UtcNow;
                        Log.Warn("Change IsUpdate = " + contact.IsUpdate);
                        contact.IsUpdate++;
                    }

                    Log.Warn("contact = " + contact.OutlookItem.FullName);
                    Log.Warn("contact mod_date= " + contact.OModifiedDate.ToString());
                    Log.Warn("UtcNow - contact.OModifiedDate= " +
                                               (int)(DateTime.UtcNow - contact.OModifiedDate).TotalSeconds);
                }
                else
                {
                    Log.Warn("not found contact. AddContactToS(oItem) ");
                }
                // oItem.Sensitivity == Outlook.OlSensitivity.olNormal
                if (IsCurrentView && ItemsSyncState.Exists(a => a.OutlookItem.EntryID == oItem.EntryID
                                                               && contact.IsUpdate == 1
                                                               && oItem.Sensitivity == Outlook.OlSensitivity.olNormal))
                {
                    Outlook.UserProperty oProp1 = oItem.UserProperties["SEntryID"];

                    if (oProp1 != null)
                    {
                        contact.IsUpdate++;
                        Log.Warn("Go to AddContactToS");
                        AddToCrm(oItem, oProp1.Value.ToString());
                    }
                    else
                    {
                        AddToCrm(oItem);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error("ThisAddIn.CItems_ItemChange", ex);
            }
            finally
            {
                Log.Warn("lContactItems.Count = " + ItemsSyncState.Count);
            }
        }

        void Items_ItemAdd(object Item)
        {
            try
            {
                if (!IsCurrentView)
                    return;

                var item = Item as Outlook.ContactItem;
                if (item.Sensitivity != Outlook.OlSensitivity.olNormal)
                {
                    ItemsSyncState.Add(new ContactSyncState { OModifiedDate = DateTime.UtcNow, OutlookItem = item });
                    Log.Warn("Contact with abnormal Sensitivity was added to lContactItems - " + item.FullName);
                    return;
                }

                //Outlook.UserProperty oProp = item.UserProperties["SOModifiedDate"];
                Outlook.UserProperty oProp2 = item.UserProperties["SEntryID"];  // to avoid duplicating of the contact
                if (oProp2 != null)
                {
                    AddToCrm(item, oProp2.Value);
                }
                else
                {
                    AddToCrm(item);
                }

            }
            catch (Exception ex)
            {
                Log.Error("ThisAddIn.CItems_ItemAdd", ex);
            }
        }
        private void AddToCrm(Outlook.ContactItem oItem, string sID = "")
        {
            if (!settings.SyncContacts)
                return;
            if (oItem != null && oItem.Sensitivity.ToString() == "olNormal")
            {
                try
                {
                    string _result = "";
                    eNameValue[] data = new eNameValue[18];

                    data[0] = clsSuiteCRMHelper.SetNameValuePair("email1", oItem.Email1Address);
                    data[1] = clsSuiteCRMHelper.SetNameValuePair("title", oItem.JobTitle);
                    data[2] = clsSuiteCRMHelper.SetNameValuePair("phone_work", oItem.BusinessTelephoneNumber);
                    data[3] = clsSuiteCRMHelper.SetNameValuePair("phone_home", oItem.HomeTelephoneNumber);
                    data[4] = clsSuiteCRMHelper.SetNameValuePair("phone_mobile", oItem.MobileTelephoneNumber);
                    data[5] = clsSuiteCRMHelper.SetNameValuePair("phone_fax", oItem.BusinessFaxNumber);
                    data[6] = clsSuiteCRMHelper.SetNameValuePair("department", oItem.Department);
                    data[7] = clsSuiteCRMHelper.SetNameValuePair("primary_address_city", oItem.BusinessAddressCity);
                    data[8] = clsSuiteCRMHelper.SetNameValuePair("primary_address_state", oItem.BusinessAddressState);
                    data[9] = clsSuiteCRMHelper.SetNameValuePair("primary_address_postalcode", oItem.BusinessAddressPostalCode);
                    data[10] = clsSuiteCRMHelper.SetNameValuePair("primary_address_country", oItem.BusinessAddressCountry);
                    data[11] = clsSuiteCRMHelper.SetNameValuePair("primary_address_street", oItem.BusinessAddressStreet);
                    data[12] = clsSuiteCRMHelper.SetNameValuePair("description", oItem.Body);
                    data[13] = clsSuiteCRMHelper.SetNameValuePair("last_name", oItem.LastName);
                    data[14] = clsSuiteCRMHelper.SetNameValuePair("first_name", oItem.FirstName);
                    data[15] = clsSuiteCRMHelper.SetNameValuePair("account_name", oItem.CompanyName);
                    data[16] = clsSuiteCRMHelper.SetNameValuePair("salutation", oItem.Title);

                    if (sID == "")
                        data[17] = clsSuiteCRMHelper.SetNameValuePair("assigned_user_id", clsSuiteCRMHelper.GetUserId());
                    else
                        data[17] = clsSuiteCRMHelper.SetNameValuePair("id", sID);

                    _result = clsSuiteCRMHelper.SetEntryUnsafe(data, "Contacts");
                    Outlook.UserProperty oProp = oItem.UserProperties["SOModifiedDate"];
                    if (oProp == null)
                        oProp = oItem.UserProperties.Add("SOModifiedDate", Outlook.OlUserPropertyType.olText);

                    oProp.Value = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");

                    Outlook.UserProperty oProp2 = oItem.UserProperties["SEntryID"];
                    if (oProp2 == null)
                        oProp2 = oItem.UserProperties.Add("SEntryID", Outlook.OlUserPropertyType.olText);
                    oProp2.Value = _result;

                    Log.Warn(oItem.FullName + " from save Sensitivity= " + oItem.Sensitivity);

                    if (oItem.Sensitivity.ToString() != "olNormal")
                        return;

                    Log.Warn("        Save");
                    oItem.Save();

                    string entryId = oItem.EntryID;
                    var sItem = ItemsSyncState.FirstOrDefault(a => a.OutlookItem.EntryID == entryId);
                    if (sItem != default(ContactSyncState))
                    {
                        sItem.OutlookItem = oItem;
                        Log.Warn("ThisAddIn.AddContactToS (DateTime.UtcNow - sItem.OModifiedDate).Milliseconds = " +
                                                   (DateTime.UtcNow - sItem.OModifiedDate).TotalSeconds.ToString());

                        sItem.OModifiedDate = DateTime.UtcNow;

                        sItem.CrmEntryId = _result;
                        Log.Warn("ThisAddIn.AddContactToS sItem.OModifiedDate = " + sItem.OModifiedDate.ToString());
                    }
                    else
                    {
                        Log.Warn("ThisAddIn.AddContactToS ADD lContactItemsFresh");
                        ItemsSyncState.Add(new ContactSyncState { CrmEntryId = _result, OModifiedDate = DateTime.UtcNow, OutlookItem = oItem });
                    }

                }
                catch (Exception ex)
                {
                    Log.Error("ThisAddIn.AddContactToS", ex);
                }
            }
        }

        void Items_ItemRemove()
        {
            try
            {
                RemoveDeletedItems(checkItemSensitivity: true);
            }
            catch (Exception ex)
            {
                Log.Error("ContactSyncing.Items_ItemRemove", ex);
            }
        }

        public Outlook.MAPIFolder GetDefaultFolder()
        {
            return Application.Session.GetDefaultFolder(Outlook.OlDefaultFolders.olFolderContacts);
        }

        protected override bool IsCurrentView => Context.CurrentFolderItemType == Outlook.OlItemType.olContactItem;

        // Should presumably be removed at some point. Existing code was ignoring deletions for Contacts and Tasks
        // (but not for Appointments).
        protected override bool PropagatesLocalDeletions => false;
    }
}
