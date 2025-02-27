﻿/* Copyright 2017 Cinegy GmbH.

  Licensed under the Apache License, Version 2.0 (the "License");
  you may not use this file except in compliance with the License.
  You may obtain a copy of the License at

    http://www.apache.org/licenses/LICENSE-2.0

  Unless required by applicable law or agreed to in writing, software
  distributed under the License is distributed on an "AS IS" BASIS,
  WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
  See the License for the specific language governing permissions and
  limitations under the License.
*/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Cinegy.TsDecoder.TransportStream;

namespace Cinegy.TsDecoder.Tables
{
    public class ServiceDescriptionTableFactory : TableFactory
    {
        public const int CurrentMuxSdtTableId = 0x42;
        public const int OtherMuxSdtTableId = 0x46;

        /// <summary>
        /// The last decoded ServiceDescription table, with the ServiceDescriptionItems associated with that table section
        /// </summary>
        public ServiceDescriptionTable ServiceDescriptionTable { get; private set; }
        
        /// <summary>
        /// An aggregated list of all current ServiceDescriptionItems, as pulled from all ServiceDescriptionTables with the same ID and version.
        /// </summary>
        public List<ServiceDescriptionItem> ServiceDescriptionItems { get; set; } = new List<ServiceDescriptionItem>();

        /// <summary>
        /// Flag that we should filter to just the SDT for current mux (if false, will filter for SDT for other mux(es) only)
        /// </summary>
        public bool CurrentMux { get; set; } = true; 

        private new ServiceDescriptionTable InProgressTable
        {
            get => base.InProgressTable as ServiceDescriptionTable;
            set => base.InProgressTable = value;
        }

        private HashSet<int> _sectionsCompleted = new HashSet<int>();

        public void AddPacket(TsPacket packet)
        {
            CheckPid(packet.Pid);

            if (packet.PayloadUnitStartIndicator)
            {
                InProgressTable = new ServiceDescriptionTable { Pid = packet.Pid, PointerField = packet.Payload[0] };

                if (InProgressTable.PointerField > packet.Payload.Length)
                {
                    Debug.Assert(true, "Service Description Table has packet pointer outside the packet.");
                }

                var pos = 1 + InProgressTable.PointerField;

                InProgressTable.VersionNumber = (byte)(packet.Payload[pos + 5] & 0x3E);
                
                InProgressTable.TableId = packet.Payload[pos];

                //Filter this SDT to only read a TableId for either current mux or other mux(es) depending on flag from well-known table IDs
                if (InProgressTable.TableId != (CurrentMux ? CurrentMuxSdtTableId : OtherMuxSdtTableId))
                {
                    InProgressTable = null;
                    return;
                }

                if (ServiceDescriptionTable?.VersionNumber != InProgressTable.VersionNumber)
                {
                    //if the version number of any section jumps, we need to refresh
                    _sectionsCompleted = new HashSet<int>();
                    ServiceDescriptionItems = new List<ServiceDescriptionItem>();
                }

                InProgressTable.SectionLength =
                    (short)(((packet.Payload[pos + 1] & 0x3) << 8) + packet.Payload[pos + 2]);
                
                InProgressTable.TransportStreamId = (ushort)((packet.Payload[pos + 3] << 8) + packet.Payload[pos + 4]);
                InProgressTable.CurrentNextIndicator = (packet.Payload[pos + 5] & 0x1) != 0;
                InProgressTable.SectionNumber = packet.Payload[pos + 6];
                InProgressTable.LastSectionNumber = packet.Payload[pos + 7];
                InProgressTable.OriginalNetworkId = (ushort)((packet.Payload[pos + 8] << 8) + packet.Payload[pos + 9]);
            }
            
            if (InProgressTable == null) return;

            if (_sectionsCompleted.Contains(InProgressTable.SectionNumber))
            {
                InProgressTable = null;
                return;
            }

            AddData(packet);

            if (!HasAllBytes()) return;

            var startOfNextField = (ushort)(InProgressTable.PointerField + 12);

            var transportStreamLoopEnd = (ushort)(InProgressTable.SectionLength - 4);

            var items = new List<ServiceDescriptionItem>();

            while (startOfNextField < transportStreamLoopEnd)
            {
                var item = new ServiceDescriptionItem
                {
                    ServiceId = (ushort)((Data[startOfNextField] << 8) + Data[startOfNextField + 1]),
                    EitScheduleFlag = ((Data[startOfNextField + 2]) & 0x02) == 0x02,
                    EitPresentFollowingFlag = ((Data[startOfNextField + 2]) & 0x01) == 0x01,
                    RunningStatus = (byte)((Data[startOfNextField + 3] >> 5) & 0x07),
                    FreeCaMode = (Data[startOfNextField + 3] & 0x10) == 0x10,
                    DescriptorsLoopLength =
                        (ushort)(((Data[startOfNextField + 3] & 0xf) << 8) + Data[startOfNextField + 4])
                };

                var descriptors = new List<Descriptor>();

                startOfNextField = (ushort)(startOfNextField + 5);
                var endOfDescriptors = (ushort)(startOfNextField + item.DescriptorsLoopLength);

                if (endOfDescriptors > Data.Length)
                {
                    throw new InvalidDataException("Descriptor data in Service Description is marked beyond available data");
                }

                while (startOfNextField < endOfDescriptors)
                {
                    var des = DescriptorFactory.DescriptorFromData(Data, startOfNextField);
                    descriptors.Add(des);
                    startOfNextField += (ushort)(des.DescriptorLength + 2);
                }
                item.Descriptors = descriptors;
                items.Add(item);
            }
            
            ServiceDescriptionItems.AddRange(items);

            ServiceDescriptionTable = InProgressTable;
            ServiceDescriptionTable.Items = ServiceDescriptionItems;

            _sectionsCompleted.Add(InProgressTable.SectionNumber);

            InProgressTable.ItemsIncomplete = false;

            for(var i = 0;i <= InProgressTable.LastSectionNumber;i++)
            {
                if (!_sectionsCompleted.Contains(i))
                    InProgressTable.ItemsIncomplete = true;
            }

            OnTableChangeDetected();
        }

    }

   
}
