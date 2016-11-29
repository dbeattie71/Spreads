﻿// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at http://mozilla.org/MPL/2.0/.

using System;
using System.Collections.Generic;

namespace Spreads.Algorithms.Math {
    // implementation of LIGO's Efficient Algorithm for computing a Running Median
    // https://dcc.ligo.org/public/0027/T030168/000/T030168-00.pdf

    public class MovingMedian {
        private readonly Comparer<RngmedValIndex> _valIndexComparer;

        public MovingMedian() {
            _valIndexComparer = new RngmedValIndexComparer(Comparer<double>.Default);
        }

        private struct Node {
            public double Data;
            public int NextSorted;
            public int NextSequence;
            public int PrevSorted;
        }

        private struct RngmedValIndex {
            public double Data;
            public int Index;
        }

        private class RngmedValIndexComparer : Comparer<RngmedValIndex> {
            private readonly Comparer<double> _comparer;

            public RngmedValIndexComparer(Comparer<double> comparer) {
                _comparer = comparer;
            }

            public override int Compare(RngmedValIndex x, RngmedValIndex y) {
                return _comparer.Compare(x.Data, y.Data);
            }
        }

        /*-------------------------------
            checks: pointers to subset of nodes to use
            as checkpoints
            ---------------------------------*/

        private int[] _sortedIndices;
        private RngmedValIndex[] _indexBlock;
        private Node[] _nodes;
        private int[] _checks;
        private int _firstSequenceIdx, _lastSequenceIdx;
        private int _currentnodeIdx = -1, _previousnodeIdx = -1;
        private int _leftnodeIdx = -1, _rightnodeIdx = -1;
        private int _reuseNextSortedIdx = -1, _reusePrevSortedIdx = -1;
        private int _dummyNodeIdx, _dummyNode1Idx, _dummyNode2Idx;
        private int _ncheckpts, _stepchkpts;
        private int _nextchkptindx;
        private int[] _checks4Shift;
        private int _nearestchk, _midpoint, _offset, _numberoffsets;
        private int _samplecount, _k, _counterChkpt, _chkcount, _shiftcounter;
        private double _nextsample, _deletesample, _dummy;
        private int _shift, _dummyInt;

        public int Rngmed(double[] data, int nblocks, double[] medians) {
            /*-----------------------------------
              Sort the first block of nblocks samples
              using the qsort function
            ------------------------------------*/
            _indexBlock = new RngmedValIndex[nblocks];
            for (_k = 0; _k < (int)nblocks; _k++) {
                _indexBlock[_k].Data = data[_k];
                _indexBlock[_k].Index = _k;
            }

            Array.Sort<RngmedValIndex>(_indexBlock, 0, nblocks, _valIndexComparer);

            _sortedIndices = new int[nblocks];
            for (_k = 0; _k < (int)nblocks; _k++) {
                _sortedIndices[_k] = _indexBlock[_k].Index;
            }

            _indexBlock = null;

            /*----------------------------------
            Indices of checkpoint nodes.
            Number of nodes per checkpoint=floor(sqrt(nblocks))
            ------------------------------------*/
            _stepchkpts = (int)System.Math.Sqrt(nblocks);
            _ncheckpts = nblocks / _stepchkpts;
            _checks = new int[_ncheckpts]; // (struct node **)LALCalloc(ncheckpts,sizeof(struct node*));
            _checks4Shift = new int[_ncheckpts];

            /*---------------------------------
              Offsets for getting median from nearest
              checkpoint: For nblocks even,
              (node(offset(1))+node(offset(2)))/2;
              for nblocks odd,
              (node(offset(1))+node(offset(1)))/2;
              THIS CAN BE OPTIMISED.
             ----------------------------------*/
            if (nblocks % 2 == 1) { // (int)System.Math.IEEERemainder(()nblocks, 2.0)) {
                /*Odd*/
                _midpoint = (nblocks + 1) / 2 - 1;
                _numberoffsets = 1;
            } else {
                /*Even*/
                _midpoint = nblocks / 2 - 1;
                _numberoffsets = 2;
            }
            _nearestchk = _midpoint / _stepchkpts; // floor(midpoint / stepchkpts);
            _offset = _midpoint - _nearestchk * _stepchkpts;

            /*----------------------------------
            Build up linked list using first nblock points
            in sequential order
            ------------------------------------*/
            _nodes = new Node[nblocks]; // (struct node **)LALCalloc(nblocks,sizeof(struct node *));

            //var first_sequence = new node(); // (struct node *)LALCalloc(1,sizeof(struct node));
            _nodes[0] = new Node();

            _firstSequenceIdx = 0;
            _nodes[0].NextSequence = -1;
            _nodes[0].NextSorted = -1;
            _nodes[0].PrevSorted = -1;
            _nodes[0].Data = data[0];

            _previousnodeIdx = _firstSequenceIdx;

            //var previousnode = first_sequence;

            for (_samplecount = 1; _samplecount < (int)nblocks; _samplecount++) {
                var currentnode = new Node();//  (struct node *)LALCalloc(1,sizeof(struct node));
                currentnode.NextSequence = -1;
                currentnode.PrevSorted = -1;
                currentnode.NextSorted = -1;
                currentnode.Data = data[_samplecount];

                _nodes[_samplecount] = currentnode;

                _nodes[_previousnodeIdx].NextSequence = _samplecount;
                // update previous node in array
                //node_addresses[samplecount - 1].next_sequence = previousnode;
                _previousnodeIdx = _samplecount;
            }
            _lastSequenceIdx = _samplecount - 1;

            /*------------------------------------
            Set the sorted sequence pointers and
            the pointers to checkpoint nodes
            -------------------------------------*/
            _currentnodeIdx = _sortedIndices[0];
            _previousnodeIdx = -1;
            _checks[0] = _currentnodeIdx;
            _nextchkptindx = _stepchkpts;
            _counterChkpt = 1;
            for (_samplecount = 1; _samplecount < (int)nblocks; _samplecount++) {
                _dummyNodeIdx = _sortedIndices[_samplecount];
                _nodes[_currentnodeIdx].NextSorted = _dummyNodeIdx;
                _nodes[_currentnodeIdx].PrevSorted = _previousnodeIdx;
                _previousnodeIdx = _currentnodeIdx;
                _currentnodeIdx = _dummyNodeIdx;
                if (_samplecount == _nextchkptindx && _counterChkpt < _ncheckpts) {
                    _checks[_counterChkpt] = _currentnodeIdx;
                    _nextchkptindx += _stepchkpts;
                    _counterChkpt++;
                }
            }
            _nodes[_currentnodeIdx].PrevSorted = _previousnodeIdx;
            _nodes[_currentnodeIdx].NextSorted = -1;
            _sortedIndices = null;

            /*------------------------------
              Get the first output element
            -------------------------------*/
            if (medians == null) {
                throw new ArgumentNullException(nameof(medians));
            }
            _currentnodeIdx = _checks[_nearestchk];
            for (_k = 1; _k <= _offset; _k++) {
                _currentnodeIdx = _nodes[_currentnodeIdx].NextSorted;
            }
            _dummy = 0;
            for (_k = 1; _k <= _numberoffsets; _k++) {
                _dummy += _nodes[_currentnodeIdx].Data;
                _currentnodeIdx = _nodes[_currentnodeIdx].NextSorted;
            }
            medians[0] = _dummy / _numberoffsets;

            /*---------------------------------
            This is the main part.
            Find the nodes whose values
            form the smallest closed interval
            around the new incoming value.
            The right limit is always >
            the new value.
            ----------------------------------*/
            for (_samplecount = nblocks; _samplecount < (int)data.Length; _samplecount++) {
                _nextsample = data[_samplecount];
                if (_nextsample >= _nodes[_checks[0]].Data) {
                    for (_chkcount = 1; _chkcount < _ncheckpts; _chkcount++) {
                        if (_nextsample >= _nodes[_checks[_chkcount]].Data) {
                        } else {
                            break;
                        }
                    }
                    _chkcount -= 1;
                    _rightnodeIdx = _checks[_chkcount];
                    _leftnodeIdx = -1; /*NEW*/
                    // NB originally it was a pointer, so we need to check for >=0 for index in array
                    // because pointer to the zero addressed node would evaluate to true in C
                    while (_rightnodeIdx >= 0) {
                        if (_nextsample < _nodes[_rightnodeIdx].Data) {
                            break;
                        }
                        _leftnodeIdx = _rightnodeIdx;
                        _rightnodeIdx = _nodes[_rightnodeIdx].NextSorted;
                    }
                } else {
                    if (_nextsample < _nodes[_checks[0]].Data) {
                        _chkcount = 0;
                        /* dummy_node=checks[0]; */
                        _rightnodeIdx = _checks[0];
                        _leftnodeIdx = -1;
                    }
                }

                /*-------------------------
                     Determine if checkpoints need to be
                     shifted or not.
                   ---------------------------*/
                _dummyNodeIdx = -1;
                if (_rightnodeIdx == _firstSequenceIdx) {
                    _dummyNodeIdx = _rightnodeIdx;
                } else if (_leftnodeIdx == _firstSequenceIdx) {
                    _dummyNodeIdx = _leftnodeIdx;
                }
                if (_dummyNodeIdx >= 0) {
                    _nodes[_dummyNodeIdx].Data = _nextsample;
                    _firstSequenceIdx = _nodes[_firstSequenceIdx].NextSequence;
                    _nodes[_dummyNodeIdx].NextSequence = -1;
                    _nodes[_lastSequenceIdx].NextSequence = _dummyNodeIdx;
                    _lastSequenceIdx = _dummyNodeIdx;
                    _shift = 0;
                } else {
                    _reuseNextSortedIdx = _rightnodeIdx;
                    _reusePrevSortedIdx = _leftnodeIdx;
                    _shift = 1; /*shift maybe required*/
                }

                /*-----------------------------------
                   Getting check points to be shifted
                 -----------------------------------*/
                // NB shift was originally int, not note*, so GT, not GE
                if (_shift > 0) {
                    _deletesample = _nodes[_firstSequenceIdx].Data;
                    if (_deletesample > _nextsample) {
                        _shiftcounter = 0;
                        for (_k = _chkcount; _k < _ncheckpts; _k++) {
                            _dummy = _nodes[_checks[_k]].Data;
                            if (_dummy > _nextsample) {
                                if (_dummy <= _deletesample) {
                                    _checks4Shift[_shiftcounter] = _k;
                                    _shiftcounter++;
                                } else {
                                    break;
                                }
                            }
                        }
                        _shift = -1; /*Left shift*/
                    } else
                          if (_deletesample <= _nextsample) {
                        _shiftcounter = 0;
                        for (_k = _chkcount; _k >= 0; _k--) {
                            _dummy = _nodes[_checks[_k]].Data;
                            if (_dummy >= _deletesample) {
                                _checks4Shift[_shiftcounter] = _k;
                                _shiftcounter++;
                            } else {
                                break;
                            }
                        }
                        _shift = 1; /*Shift Right*/
                    }
                }

                /*------------------------------
                 Recycle the node with the
                 oldest value.
                --------------------------------*/
                if (_shift > 0) {
                    /*---------------------
                     Reset sequential links
                     ---------------------*/
                    _dummyNodeIdx = _firstSequenceIdx;
                    _firstSequenceIdx = _nodes[_dummyNodeIdx].NextSequence;
                    _nodes[_dummyNodeIdx].NextSequence = -1;
                    _nodes[_lastSequenceIdx].NextSequence = _dummyNodeIdx;
                    _lastSequenceIdx = _dummyNodeIdx;
                    _nodes[_dummyNodeIdx].Data = _nextsample;
                    _dummyNode1Idx = _nodes[_dummyNodeIdx].PrevSorted;
                    _dummyNode2Idx = _nodes[_dummyNodeIdx].NextSorted;
                    /*-----------------------
                      Repair deletion point
                    ------------------------*/
                    if (_dummyNode1Idx == -1) {
                        _nodes[_dummyNode2Idx].PrevSorted = _dummyNode1Idx;
                    } else {
                        if (_dummyNode2Idx == -1) {
                            _nodes[_dummyNode1Idx].NextSorted = _dummyNode2Idx;
                        } else {
                            _nodes[_dummyNode1Idx].NextSorted = _dummyNode2Idx;
                            _nodes[_dummyNode2Idx].PrevSorted = _dummyNode1Idx;
                        }
                    }
                    /*------------------------
                      Set pointers from neighbours to new node at insertion point
                    -------------------------*/
                    if (_rightnodeIdx == -1) {
                        _nodes[_leftnodeIdx].NextSorted = _dummyNodeIdx;
                    } else {
                        if (_leftnodeIdx == -1) {
                            _nodes[_rightnodeIdx].PrevSorted = _dummyNodeIdx;
                        } else {
                            _nodes[_leftnodeIdx].NextSorted = _dummyNodeIdx;
                            _nodes[_rightnodeIdx].PrevSorted = _dummyNodeIdx;
                        }
                    }

                    /*-------------------------------
                      Shift check points before resetting sorted list
                    --------------------------------*/
                    if (_shift == -1) {
                        for (_k = 0; _k < _shiftcounter; _k++) {
                            _dummyInt = _checks4Shift[_k];
                            _checks[_dummyInt] = _nodes[_checks[_dummyInt]].PrevSorted;
                        }
                    } else
                           if (_shift == 1) {
                        for (_k = 0; _k < _shiftcounter; _k++) {
                            _dummyInt = _checks4Shift[_k];
                            _checks[_dummyInt] = _nodes[_checks[_dummyInt]].NextSorted;
                        }
                    }

                    /*--------------------------------
                      insert node
                     --------------------------------*/
                    _nodes[_dummyNodeIdx].NextSorted = _reuseNextSortedIdx;
                    _nodes[_dummyNodeIdx].PrevSorted = _reusePrevSortedIdx;
                }

                /*--------------------------------
                  Get the median
                ---------------------------------*/
                _currentnodeIdx = _checks[_nearestchk];
                for (_k = 1; _k <= _offset; _k++) {
                    _currentnodeIdx = _nodes[_currentnodeIdx].NextSorted;
                }
                _dummy = 0;
                for (_k = 1; _k <= _numberoffsets; _k++) {
                    _dummy += _nodes[_currentnodeIdx].Data;
                    _currentnodeIdx = _nodes[_currentnodeIdx].NextSorted;
                }
                medians[_samplecount - nblocks + 1] = _dummy / _numberoffsets;
            }

            return 0;
        }
    }
}
